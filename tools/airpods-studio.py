"""Offline lighting for transparent device artwork."""

import subprocess
from tempfile import TemporaryFile

import cv2
import numpy as np


cv2.setNumThreads(1)

SHELL_STRENGTH = {
    "airpods1": .91, "airpods2": .91, "airpods3": 1,
    "airpods4": .92, "airpods4-anc": .92, "pro1": .93, "pro2": 1,
}

# Leave room between diffuse white and the source reflections. The old
# exposure lift compressed these into the same range on the older shells.
SHELL_TONES = np.array([
    (0, 0), (24, 24), (64, 66), (108, 113), (148, 155), (176, 187),
    (193, 213), (206, 234), (218, 251), (240, 254), (255, 255),
], dtype=np.float32)


def smoothstep(value, low, high):
    t = np.clip((value - low) / (high - low), 0, 1)
    return t * t * (3 - 2 * t)


def light_shell(frame, slug):
    strength = SHELL_STRENGTH.get(slug, 0)
    if not strength:
        return frame

    height, width = frame.shape[:2]
    rgb = frame[..., :3].astype(np.float32)
    luminance = rgb @ np.array([.2126, .7152, .0722], dtype=np.float32)
    neutral = 1 - smoothstep(rgb.max(axis=2) - rgb.min(axis=2), 14, 48)
    glaze = smoothstep(luminance, 108, 167) * neutral

    # The silhouette controls where a surface faces each light. Work on a
    # softened half-size field so reflections move smoothly around the curve.
    alpha = cv2.resize(frame[..., 3], (max(2, width // 2), max(2, height // 2)), interpolation=cv2.INTER_AREA)
    mask = (alpha > 112).astype(np.uint8)
    _, top, _, extent = cv2.boundingRect(mask)
    if not extent:
        return frame
    distance = cv2.distanceTransform(np.pad(mask, 1), cv2.DIST_L2, 5)[1:-1, 1:-1]
    radius = max(1.25, extent * .024)
    distance = cv2.GaussianBlur(distance, (0, 0), radius)
    left = np.maximum(0, cv2.Sobel(distance, cv2.CV_32F, 1, 0, ksize=3) / 8)
    right = np.maximum(0, -cv2.Sobel(distance, cv2.CV_32F, 1, 0, ksize=3) / 8)
    down = np.maximum(0, -cv2.Sobel(distance, cv2.CV_32F, 0, 1, ksize=3) / 8)
    rim = np.exp(-.5 * ((distance / extent - .060) / .038) ** 2)
    falloff = smoothstep(np.arange(mask.shape[0], dtype=np.float32)[:, None], top, top + extent)
    lighting = rim * (17 * left + 10 * right - 10 * down) + 6 - 13 * falloff
    lighting = cv2.resize(lighting, (width, height), interpolation=cv2.INTER_LINEAR)

    mapped = np.interp(luminance, SHELL_TONES[:, 0], SHELL_TONES[:, 1]).astype(np.float32)
    # Protect tinted LEDs, dark grilles and the molded seam. Taper the added
    # light into the highlights rather than clipping the white plastic.
    highlight_room = 1 - smoothstep(mapped, 226, 255)
    delta = neutral * (mapped - luminance) + glaze * lighting * highlight_room
    rgb += (strength * delta)[..., None]
    rgb += (strength * glaze * (1 - smoothstep(luminance, 160, 221)))[..., None] * np.array([-1.6, -.5, 1.3], dtype=np.float32)
    result = frame.copy()
    result[..., :3] = np.clip(np.rint(rgb), 0, 255).astype(np.uint8)
    return result


def read_frames(ffmpeg, source, size, filter_graph, *, start=0, end=None):
    width, height = size
    filters = f"{filter_graph},fps=60,format=rgba,trim=start_frame={start}"
    if end is not None:
        filters += f":end_frame={end}"
    with TemporaryFile() as errors, subprocess.Popen([
        ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-filter_threads", "1",
        "-threads", "2", "-i", str(source), "-an", "-vf", f"{filters},setpts=PTS-STARTPTS",
        "-c:v", "rawvideo", "-threads", "1", "-f", "rawvideo", "pipe:1",
    ], stdout=subprocess.PIPE, stderr=errors) as process:
        try:
            frame_size = width * height * 4
            while data := process.stdout.read(frame_size):
                if len(data) != frame_size:
                    raise ValueError(f"Incomplete frame from {source.name}")
                yield np.frombuffer(data, np.uint8).reshape(height, width, 4)
            check_exit(process, errors)
        finally:
            if process.poll() is None:
                process.kill()


def check_exit(process, errors):
    if process.wait():
        errors.seek(0)
        raise ValueError(errors.read().decode(errors="replace").strip())


def write_clip(ffmpeg, destination, size, frames):
    width, height = size
    with TemporaryFile() as errors, subprocess.Popen([
        ffmpeg, "-hide_banner", "-v", "error", "-nostdin", "-filter_threads", "1",
        "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", f"{width}x{height}",
        "-framerate", "60", "-i", "pipe:0", "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p",
        "-b:v", "0", "-crf", "20", "-threads", "2", "-row-mt", "1", "-deadline", "good",
        "-cpu-used", "6", str(destination),
    ], stdin=subprocess.PIPE, stderr=errors) as process:
        try:
            with process.stdin as stream:
                for frame in frames:
                    stream.write(frame)
        except BrokenPipeError:
            check_exit(process, errors)
            raise
        except BaseException:
            process.kill()
            raise
        check_exit(process, errors)


def reframe(frame, camera):
    zoom, x, y = camera
    if (zoom, x, y) == (1, 0, 0):
        return frame
    height, width = frame.shape[:2]
    premultiplied = frame.astype(np.float32)
    premultiplied[..., :3] *= premultiplied[..., 3:4] / 255
    matrix = np.array([[zoom, 0, x], [0, zoom, y]], dtype=np.float32)
    projected = cv2.warpAffine(premultiplied, matrix, (width, height), flags=cv2.INTER_LINEAR)
    projected[..., :3] *= 255 / np.maximum(projected[..., 3:4], 1)
    return np.clip(np.rint(projected), 0, 255).astype(np.uint8)
