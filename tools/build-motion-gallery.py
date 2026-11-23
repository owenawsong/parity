"""Prepare the local Pro 3 gallery with Pillow, NumPy, OpenCV and alpha-capable FFmpeg."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from io import BytesIO
from pathlib import Path
import plistlib
import runpy
import shutil
import subprocess
from tempfile import TemporaryDirectory
from zipfile import ZipFile

from PIL import Image

LIFT_HOLD_FRAME = 56
ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "dist" / "motion-gallery"
MEDIA = OUTPUT / "media"
PRO3_CONNECTION = ROOT / ".build" / "pro3-connect"
LIFT_SOURCE_SIZE = (432, 480)
pro3_import = runpy.run_path(str(ROOT / "tools" / "import-pro3-art.py"))
bundle_file = pro3_import["bundle_file"]
export_hardware = pro3_import["export_hardware"]

# Framing preserves the source composition, including the space between the buds and case.
CLIPS = {
    "loop-charged": "crop=940:440:54:322,scale=470:220",
    "connect-engage": "scale=540:600",
    "connect-loop": "crop=920:852:80:180,scale=460:426",
    "loop-charging": "crop=940:440:54:322,scale=470:220",
    "repair-loop": "crop=680:680:185:200,scale=340:340",
    "features": "scale=432:480,fps=30",
}

VP9 = [
    "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-b:v", "0",
    "-deadline", "good", "-cpu-used", "5", "-threads", "3", "-row-mt", "1",
]


def run(command, **kwargs):
    result = subprocess.run(command, capture_output=True, **kwargs)
    if result.returncode:
        raise RuntimeError(result.stderr.decode(errors="replace").strip())
    return result.stdout


def export_clip(name, source, framing, ffmpeg, stage):
    command = [
        ffmpeg, "-hide_banner", "-v", "error", "-threads", "2", "-i", str(source),
        "-an", "-vf", f"{framing}:flags=lanczos" if ",fps=" not in framing else framing,
    ]
    poster = run([
        *command, "-frames:v", "1", "-pix_fmt", "rgba", "-c:v", "png", "-f", "image2pipe", "pipe:1",
    ])
    with Image.open(BytesIO(poster)) as image:
        if image.mode != "RGBA" or image.getchannel("A").getextrema() != (0, 255):
            raise ValueError(f"{name} must retain its alpha channel. Use FFmpeg 9 or newer.")
        image.save(stage / f"{name}.webp", "WEBP", quality=94, alpha_quality=100)
    run([*command, *VP9, "-crf", "22", str(stage / f"{name}.webm")])


def export_rig(ffmpeg, stage):
    with Image.open(ROOT / "ui/src/assets/airpods-pro-3.webp") as atlas:
        frames = []
        for index in range(60, 80):
            x, y = index % 5 * 384, index // 5 * 212
            frames.append(atlas.crop((x, y, x + 384, y + 212)).convert("RGBA"))
    frames[-1].save(stage / "latchwave-rig.webp", "WEBP", quality=94)
    timeline = [frames[0]] * 9 + frames + [frames[-1]] * 24 + frames[-2::-1]
    run([
        ffmpeg, "-hide_banner", "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba",
        "-video_size", "384x212", "-framerate", "30", "-i", "pipe:0",
        *VP9, "-crf", "22", str(stage / "latchwave-rig.webm"),
    ], input=b"".join(frame.tobytes() for frame in timeline))


def export_lift_fade(source, ffmpeg, stage):
    """Fade the two-bud pose as one layer before Apple's individual fade begins."""
    width, height = LIFT_SOURCE_SIZE
    hold_frame, clear_frame = LIFT_HOLD_FRAME, 78
    frame_size = width * height * 4
    raw = run([
        ffmpeg, "-hide_banner", "-v", "error", "-i", str(source), "-an",
        "-vf", f"scale={width}:{height}:flags=lanczos", "-frames:v", str(hold_frame + 1),
        "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1",
    ])
    if len(raw) != (hold_frame + 1) * frame_size:
        raise ValueError("The feature clip did not supply the frames for the lift-out cut")

    frame = Image.frombytes("RGBA", (width, height), raw[-frame_size:])
    alpha = frame.getchannel("A")
    timeline = [raw]
    for index in range(hold_frame + 1, clear_frame):
        progress = (index - hold_frame) / (clear_frame - hold_frame)
        opacity = 1 - progress ** 2
        frame.putalpha(alpha.point([round(level * opacity) for level in range(256)]))
        timeline.append(frame.tobytes())
    timeline.append(bytes(frame_size))
    run([
        ffmpeg, "-hide_banner", "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba",
        "-video_size", f"{width}x{height}", "-framerate", "60", "-i", "pipe:0",
        *VP9, "-crf", "0", "-lossless", "1", str(stage / "lift-out-fade.webm"),
    ], input=b"".join(timeline))


def export_framed_lift(source, ffmpeg, stage):
    """Fit the whole motion on one transparent canvas without zooming between frames."""
    width, height = LIFT_SOURCE_SIZE
    frame_size = width * height * 4
    raw = run([
        ffmpeg, "-hide_banner", "-v", "error", "-c:v", "libvpx-vp9", "-i", str(source),
        "-an", "-threads", "1", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1",
    ])
    frames = [
        Image.frombytes("RGBA", (width, height), raw[offset:offset + frame_size])
        for offset in range(0, len(raw), frame_size)
    ]
    bounds = [box for frame in frames if (box := frame.getbbox(alpha_only=True))]
    if not bounds or frames[-1].getbbox(alpha_only=True) is not None:
        raise ValueError("The lift-out clip should contain visible artwork and a transparent end frame")
    crop = (
        min(box[0] for box in bounds), min(box[1] for box in bounds),
        max(box[2] for box in bounds), max(box[3] for box in bounds),
    )
    padding = 12
    size = tuple((crop[axis + 2] - crop[axis] + 2 * padding + 1) & ~1 for axis in (0, 1))

    def frame_artwork(frame):
        canvas = Image.new("RGBA", size)
        canvas.paste(frame.crop(crop), (padding, padding))
        return canvas

    frame_artwork(frames[0]).save(stage / "lift-out.webp", "WEBP", quality=94, alpha_quality=100)
    run([
        ffmpeg, "-hide_banner", "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba",
        "-video_size", f"{size[0]}x{size[1]}", "-framerate", "60", "-i", "pipe:0",
        *VP9, "-crf", "0", "-lossless", "1", str(stage / "lift-out.webm"),
    ], input=b"".join(frame_artwork(frame).tobytes() for frame in frames))


def export_app_lift(source, ffmpeg, output):
    """Author the slower desktop lift at 60 fps, preserving transparency during motion."""
    import cv2
    import numpy as np

    cv2.setNumThreads(2)
    with Image.open(source.with_suffix(".webp")) as poster:
        width, height = poster.size
    pixels = run([
        ffmpeg, "-v", "error", "-c:v", "libvpx-vp9", "-i", str(source),
        "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1",
    ])
    frames = np.frombuffer(pixels, np.uint8).reshape(-1, height, width, 4)
    solver = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
    solver.setFinestScale(0)
    y, x = np.mgrid[:height, :width].astype(np.float32)
    grid = np.stack((x, y), axis=-1)
    cache = {}

    def prepare(frame):
        rgba = frame.astype(np.float32)
        rgba[:, :, :3] *= rgba[:, :, 3:4] / 255
        gray = cv2.cvtColor(rgba[:, :, :3].astype(np.uint8), cv2.COLOR_RGB2GRAY)
        return rgba, gray

    timeline = []
    # Four output frame intervals for every three authored intervals.
    for index in range((len(frames) - 1) * 4 // 3 + 1):
        first, quarter = divmod(index * 3, 4)
        if quarter == 0:
            timeline.append(frames[first].tobytes())
            continue
        if first >= LIFT_HOLD_FRAME:
            # The final authored pose is stationary. Interpolate its opacity only.
            sample = frames[first].copy()
            sample[:, :, 3] = np.rint((frames[first, :, :, 3].astype(np.float32) * (4 - quarter)
                                       + frames[first + 1, :, :, 3].astype(np.float32) * quarter) / 4)
            timeline.append(sample.tobytes())
            continue
        if first not in cache:
            a, gray_a = prepare(frames[first])
            b, gray_b = prepare(frames[first + 1])
            forward = solver.calc(gray_a, gray_b, None)
            backward = solver.calc(gray_b, gray_a, None)
            cache = {first: (a, b, forward, backward)}
        a, b, forward, backward = cache[first]
        t = quarter / 4
        earlier = cv2.remap(a, grid - t * forward, None, cv2.INTER_LINEAR)
        later = cv2.remap(b, grid - (1 - t) * backward, None, cv2.INTER_LINEAR)
        sample = earlier * (1 - t) + later * t
        sample[:, :, :3] *= 255 / np.maximum(sample[:, :, 3:4], 1)
        timeline.append(np.clip(np.rint(sample), 0, 255).astype(np.uint8).tobytes())
    run([
        ffmpeg, "-y", "-v", "error", "-f", "rawvideo", "-pixel_format", "rgba",
        "-video_size", f"{width}x{height}", "-framerate", "60", "-i", "pipe:0",
        *VP9, "-crf", "0", "-lossless", "1", str(output),
    ], input=b"".join(timeline))


def publish_framed_lift(stage, ffmpeg):
    PRO3_CONNECTION.mkdir(parents=True, exist_ok=True)
    for suffix in ("webm", "webp"):
        asset = stage / f"lift-out.{suffix}"
        app_copy = stage / f"app-{asset.name}"
        if suffix == "webm":
            export_app_lift(asset, ffmpeg, app_copy)
        else:
            shutil.copyfile(asset, app_copy)
        app_copy.replace(PRO3_CONNECTION / asset.name)
        asset.replace(MEDIA / f"lift-out-fade.{suffix}")


def retime_lift(source, ffmpeg, frames):
    """Interpolate in premultiplied color so moving transparent edges stay clean."""
    width, height = LIFT_SOURCE_SIZE
    stride = width * height * 4
    raw = run([
        ffmpeg, "-hide_banner", "-v", "error", "-c:v", "libvpx-vp9",
        "-i", str(source), "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1",
    ])
    samples = [raw[start:start + stride] for start in range(0, len(raw), stride)]
    premultiplied = [
        Image.frombytes("RGBA", (width, height), sample).convert("RGBa")
        for sample in samples
    ]
    timeline = []
    for index in range(frames):
        earlier, fraction = divmod(index * (len(samples) - 1), frames - 1)
        if fraction:
            frame = Image.blend(
                premultiplied[earlier], premultiplied[earlier + 1], fraction / (frames - 1),
            ).convert("RGBA")
            timeline.append(frame.tobytes())
        else:
            timeline.append(samples[earlier])
    return b"".join(timeline)


def export_pairing_lift(source, ffmpeg, stage):
    """Speed up the reveal and ease the lift-out while delivering a 60 FPS cut."""
    source_reveal_frames, reveal_frames, lift_frames = 381, 218, 98
    reveal_spacing = f"{reveal_frames - 1}/{source_reveal_frames - 1}"
    filters = (
        f"[0:v]select='not(n)+gt(floor(n*{reveal_spacing}+0.5),"
        f"floor((n-1)*{reveal_spacing}+0.5))',"
        "scale=432:480:flags=lanczos,format=rgba,setsar=1,settb=1/60,setpts=N[reveal];"
        "[1:v]format=rgba,setsar=1,settb=1/60,setpts=N[lift];"
        "[reveal][lift]concat=n=2:v=1:a=0,format=yuva420p[cut]"
    )
    run([
        ffmpeg, "-hide_banner", "-v", "error", "-i", str(source),
        "-f", "rawvideo", "-pixel_format", "rgba", "-video_size", "432x480",
        "-framerate", "60", "-i", "pipe:0", "-filter_complex", filters,
        "-map", "[cut]", "-an", "-r", "60", *VP9, "-crf", "0", "-lossless", "1",
        str(stage / "pairing-lift-test.webm"),
    ], input=retime_lift(stage / "lift-out-fade.webm", ffmpeg, lift_frames))
    shutil.copyfile(stage / "connect-engage.webp", stage / "pairing-lift-test.webp")


def export_waiting_motion(reveal, rotation, ffmpeg, output):
    """Join the reveal to the same pose in the open-case rotation at 60 FPS."""
    output.mkdir(parents=True, exist_ok=True)
    framing = "crop=920:680:80:280,scale=460:340:flags=lanczos,setsar=1,format=yuva420p"
    # Reveal frame 144 matches rotation frame 123. Start the loop on the next frame.
    intro = [
        ffmpeg, "-hide_banner", "-v", "error", "-y", "-threads", "2", "-i", str(reveal), "-an",
        "-vf", f"trim=start_frame=30:end_frame=145,setpts=PTS-STARTPTS,{framing}",
    ]
    poster = run([*intro, "-frames:v", "1", "-pix_fmt", "rgba", "-c:v", "png", "-f", "image2pipe", "pipe:1"])
    with Image.open(BytesIO(poster)) as image:
        if image.getchannel("A").getextrema() != (0, 255):
            raise ValueError("The waiting animation needs an alpha-capable FFmpeg decoder")
        image.save(output / "pro3-opening.webp", "WEBP", quality=94, alpha_quality=100)
    # Stabilize at native resolution, before downsampling. The middle walls
    # of the white shell stay visible from every angle; the open lid does not.
    def stabilize(source, name, filters, opening):
        import numpy as np
        width, height = 920, 680
        decoder = [ffmpeg, "-v", "error", "-threads", "2", "-i", str(source), "-an",
                   "-vf", filters + ",crop=920:680:80:280,format=rgba", "-f", "rawvideo", "pipe:1"]
        encoder = [ffmpeg, "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgba",
                   "-s", f"{width}x{height}", "-r", "60", "-i", "pipe:0", "-vf",
                   "scale=460:340:flags=lanczos,setsar=1", *VP9, "-lossless", "1", str(output / name)]
        with subprocess.Popen(decoder, stdout=subprocess.PIPE) as dec, subprocess.Popen(encoder, stdin=subprocess.PIPE) as enc:
            number = 0
            while data := dec.stdout.read(width * height * 4):
                frame = np.frombuffer(data, dtype=np.uint8).reshape(height, width, 4)
                # The opening has its own camera move. Ease into registration
                # only over its final frames, once the case is upright.
                amount = min(1., max(0., (number - 88) / 25.)) if opening else 1.
                if amount:
                    alpha = frame[:, :, 3]
                    wall = alpha[334:458] > 128
                    middle = (wall.argmax(axis=1) + width - 1 - wall[:, ::-1].argmax(axis=1)).mean() / 2
                    bottom = np.flatnonzero((alpha > 128).any(axis=1))[-1]
                    # Match the existing lift's start pose (loop frame 236).
                    dx, dy = round((464. - middle) * amount), round((587. - bottom) * amount)
                    frame = np.roll(frame, (dy, dx), axis=(0, 1))
                enc.stdin.write(frame.tobytes())
                number += 1
            enc.stdin.close()
            if dec.wait() or enc.wait() or number != (115 if opening else 360):
                raise ValueError(f"Incomplete waiting motion: {name} ({number} frames)")

    stabilize(reveal, "pro3-opening.webm", "trim=start_frame=30:end_frame=145,setpts=PTS-STARTPTS", True)
    stabilize(rotation, "pro3-waiting.webm", "split[h][t];[t]trim=start_frame=124,setpts=PTS-STARTPTS[l];"
              "[h]trim=end_frame=124,setpts=PTS-STARTPTS[e];[l][e]concat=n=2:v=1:a=0", False)



def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    args = parser.parse_args()
    MEDIA.mkdir(parents=True, exist_ok=True)

    with TemporaryDirectory(dir=OUTPUT) as temporary:
        stage = Path(temporary)
        with ZipFile(args.bundle) as bundle:
            properties = plistlib.loads(bundle.read("Info.plist"))["MobileAssetProperties"]
            if properties.get("ProductType") != "AirPodsPro1,3-v2" or properties.get("VariantName") != "White":
                raise ValueError("Use Apple's white AirPods Pro 3 bundle")
            export_hardware(bundle, stage)
            for name in CLIPS:
                (stage / f"{name}.mov").write_bytes(bundle_file(bundle, f"ProxCard_{name}.mov"))

        with ThreadPoolExecutor(max_workers=3) as pool:
            conversions = [
                pool.submit(export_clip, name, stage / f"{name}.mov", framing, args.ffmpeg, stage)
                for name, framing in CLIPS.items()
            ]
            conversions.append(pool.submit(export_rig, args.ffmpeg, stage))
            conversions.append(pool.submit(export_lift_fade, stage / "features.mov", args.ffmpeg, stage))
            for conversion in conversions:
                conversion.result()

        export_pairing_lift(stage / "connect-engage.mov", args.ffmpeg, stage)
        export_waiting_motion(stage / "connect-engage.mov", stage / "connect-loop.mov", args.ffmpeg, PRO3_CONNECTION)
        export_framed_lift(stage / "lift-out-fade.webm", args.ffmpeg, stage)
        publish_framed_lift(stage, args.ffmpeg)
        for name in (*CLIPS, "latchwave-rig", "pairing-lift-test"):
            for suffix in ("webp", "webm"):
                filename = f"{name}.{suffix}"
                (stage / filename).replace(MEDIA / filename)
        for hardware in stage.glob("pro3-*.webp"):
            hardware.replace(MEDIA / hardware.name)

    shutil.copytree(ROOT / "ui/preview/motion-gallery", OUTPUT, dirs_exist_ok=True)
    previews = OUTPUT / "preview"
    previews.mkdir(exist_ok=True)
    for name in ("Pairside.html", "Latchwave Popup.html"):
        shutil.copyfile(ROOT / "dist" / name, previews / name)
    print(OUTPUT)


if __name__ == "__main__":
    main()
