"""Prepare local Overview artwork and matched connection clips from cached Apple bundles."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from io import BytesIO
from itertools import chain
import json
from pathlib import Path
import plistlib
import runpy
from tempfile import TemporaryDirectory
from zipfile import ZipFile

import cv2
import numpy as np
from PIL import Image, ImageOps

SOURCE = runpy.run_path(str(Path(__file__).with_name("import-airpods-popup-models.py")))
STUDIO = runpy.run_path(str(Path(__file__).with_name("airpods-studio.py")))
BUILD = SOURCE["BUILD"]
THUMB = 96
SAMPLE_RATE = 60
WIDTH, HEIGHT = 560, 432
SIZE = WIDTH, HEIGHT
HEADPHONE_SIZE = 536, 560
# On these models the stock isolated art shows the outward grilles. At this
# point in the turn both buds are separated and their tips are visible.
INWARD_BUD_POSE = {"pro1": .48, "pro2": .48}
UPRIGHT_START = 31
CLASSIC_FRONT = 274
cv2.setNumThreads(1)


def source_members(bundle):
    return {Path(name).name: name for name in bundle.namelist() if ".devicebundle/" in name and not name.endswith("/")}


def image_data(ffmpeg, source, framing=None, time=0):
    filters = ("-vf", framing) if framing else ()
    return SOURCE["run_ffmpeg"](
        ffmpeg, "-threads", "2", "-ss", str(time), "-i", str(source), *filters, "-frames:v", "1",
        "-c:v", "png", "-threads", "1", "-pix_fmt", "rgba", "-f", "image2pipe", "pipe:1",
    )


def save_photo(image, destination, crop=True):
    alpha = image.getchannel("A")
    bounds = alpha.point(lambda value: 255 if value >= 5 else 0).getbbox()
    if not bounds or alpha.getextrema()[0] != 0:
        raise ValueError(f"{destination.name} is missing transparent product art")
    (image.crop(bounds) if crop else image).save(destination, quality=94, alpha_quality=100)


def studio_photo(image, slug):
    # Use the silhouette before trimming its transparent margin.
    return Image.fromarray(STUDIO["light_shell"](np.asarray(image), slug))


def inward_buds(ffmpeg, source, time):
    frame = Image.open(BytesIO(image_data(ffmpeg, source, time=time))).convert("RGBA")
    alpha = np.asarray(frame.getchannel("A"))
    occupied = np.r_[False, (alpha >= 8).any(axis=0), False]
    boundaries = np.flatnonzero(occupied[:-1] != occupied[1:])
    spans = [(int(left), int(right)) for left, right in boundaries.reshape(-1, 2) if right - left > 20]
    if len(spans) != 3:
        raise ValueError(f"{source.name} does not show two separate buds and a case at {time}s")
    return {part: frame.crop((left - 2, 0, right + 2, frame.height))
            for part, (left, right) in zip(("left", "right"), spans)}


def samples(ffmpeg, source, seconds, framing=None):
    filters = f"fps={SAMPLE_RATE},scale={THUMB}:{THUMB},format=rgba"
    data = SOURCE["run_ffmpeg"](
        ffmpeg, "-threads", "2", "-i", str(source), "-t", str(seconds), "-an",
        "-vf", f"{framing},{filters}" if framing else filters,
        "-threads", "1", "-c:v", "rawvideo", "-f", "rawvideo", "pipe:1",
    )
    if not data:
        raise ValueError(f"No decoded frames for {source.name}")
    frames = np.frombuffer(data, np.uint8).reshape(-1, THUMB, THUMB, 4)
    if frames[..., 3].min() != 0 or frames[0, ..., 3].max() < 192:
        raise ValueError(f"{source.name} has no usable alpha channel")
    return frames


def silhouette(frames):
    result = frames.astype(np.float32) / 255
    result[..., :3] *= result[..., 3:4]
    return result


def cut_and_frame(opening, turn, reveal, start, include_setup=False):
    """Find the same pose and direction of travel anywhere in the original loop."""
    entry = silhouette(opening[:, ::3, ::3]).reshape(len(opening), -1)
    loop = silhouette(turn[:, ::3, ::3]).reshape(len(turn), -1)
    error = ((entry ** 2).mean(axis=1)[:, None] + (loop ** 2).mean(axis=1)
             - 2 / entry.shape[1] * (entry @ loop.T))
    offsets = (-3, -2, -1, 0, 1, 2, 3)
    motion = sum(np.roll(error, (-offset, -offset), axis=(0, 1)) for offset in offsets) / len(offsets)
    motion[:max(3, start + 48)] = np.inf
    motion[-3:] = np.inf
    # Hand off shortly after the lid opens when both clips already match.
    # A later turn is only worth using if the earlier pose is visibly different.
    best = motion.min()
    if not np.isfinite(best):
        raise ValueError("No complete pose match in the opening sequence")
    cut = int(np.flatnonzero(motion.min(axis=1) <= best + .00006)[0])
    loop_pose = int(np.argmin(motion[cut]))
    loop_start = (int(loop_pose) + 1) % len(turn)
    first = 0 if include_setup else start
    mask = (opening[first:cut + 1, ..., 3] > 8).any(axis=0) | (turn[..., 3] > 8).any(axis=0)
    if reveal is not None:
        mask |= (reveal[..., 3] > 8).any(axis=0)
    ys, xs = np.nonzero(mask)
    x0, y0 = max(0, xs.min() - 5), max(0, ys.min() - 5)
    x1, y1 = min(THUMB, xs.max() + 6), min(THUMB, ys.max() + 6)
    left, top = f"floor(iw*{x0}/{THUMB}/2)*2", f"floor(ih*{y0}/{THUMB}/2)*2"
    right, bottom = f"ceil(iw*{x1}/{THUMB}/2)*2", f"ceil(ih*{y1}/{THUMB}/2)*2"
    crop = f"crop={right}-{left}:{bottom}-{top}:{left}:{top}"
    return int(cut) + 1, loop_start, crop


def fixed_canvas(crop, size):
    canvas = f"{size[0]}:{size[1]}"
    return (f"{crop},scale={canvas}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,"
            f"format=yuva420p,pad={canvas}:(ow-iw)/2:(oh-ih)/2:color=black@0")


def waiting_camera(frames, opening, size, portrait):
    # Fit the entire turn once so the apparent scale doesn't pump with yaw.
    bounds = np.array([cv2.boundingRect((frame[..., 3] > 20).astype(np.uint8)) for frame in frames])
    left, top = bounds[:, 0].min(), bounds[:, 1].min()
    right, bottom = (bounds[:, :2] + bounds[:, 2:]).max(axis=0)
    center_x, center_y = (left + right) / (2 * THUMB), (top + bottom) / (2 * THUMB)
    ys, xs = np.nonzero((opening[..., 3] > 20).any(axis=0))
    extent_x = max(center_x - xs.min() / THUMB, (xs.max() + 1) / THUMB - center_x)
    extent_y = max(center_y - ys.min() / THUMB, (ys.max() + 1) / THUMB - center_y)
    height_fit = .84 if portrait else .73
    width_fit = .91 if portrait else .84
    zoom = min(height_fit * THUMB / np.median(bounds[:, 3]), width_fit * THUMB / (right - left),
               .88 * THUMB / (bottom - top), .46 / extent_x, .46 / extent_y, 1.85)
    return float(zoom), (.5 - zoom * center_x) * size[0], (.5 - zoom * center_y) * size[1]


def studio_frame(pixels, slug, camera):
    return STUDIO["reframe"](STUDIO["light_shell"](pixels, slug), camera)


def ease(value):
    t = min(1., max(0., value))
    return t ** 3 * (10 + t * (-15 + 6 * t))


def reveal_cameras(frames, camera):
    # Keep the turn's magnification. Only follow the rising buds, and let the
    # falling case leave through a soft matte at the bottom of the stage.
    zoom, x, y = camera
    top = np.array([cv2.boundingRect((frame[..., 3] > 20).astype(np.uint8))[1] for frame in frames])
    top = top * zoom * HEIGHT / THUMB + y
    clearance = np.maximum.accumulate(np.maximum(0, .06 * HEIGHT - top))
    ahead = clearance[np.minimum(np.arange(len(clearance)) + 7, len(clearance) - 1)]
    pan = cv2.GaussianBlur(ahead[None, :], (0, 0), 5, borderType=cv2.BORDER_REPLICATE)[0]
    return [(zoom, x, y + offset * ease(index / 14)) for index, offset in enumerate(pan)]


def reveal_frames_on_stage(frames, slug, cameras, duration):
    rows = np.linspace(0, 1, HEIGHT, dtype=np.float32)[:, None]
    # Start the Pro 3 fade before the source dims one bud on its own.
    # Keep its real tilt continuous through the exit.
    fade_length = .32 if slug == "pro3" else .26
    for index, source in enumerate(frames):
        time = index / SAMPLE_RATE
        frame = studio_frame(source, slug, cameras[min(index, len(cameras) - 1)]).copy()
        opacity = 1 - ease((time - duration + fade_length) / (fade_length - 1 / SAMPLE_RATE))
        # Keep the case's fade near the bottom while it drops. Once the
        # earbuds are clear, leave only a narrow fade at the very edge.
        floor = STUDIO["smoothstep"](rows, .80 + .14 * ease((time - .54) / .21), .985)
        floor_strength = ease(time / .14)
        frame[..., 3] = np.rint(frame[..., 3] * opacity * (1 - floor * floor_strength)).astype(np.uint8)
        yield frame


def classic_reveal(ffmpeg, backdrop, left, right, destination):
    """Lift the isolated earbuds through the photographed front rim of the open case."""
    width, height = backdrop.size
    x0, y0, x1, y1 = backdrop.getchannel("A").point(lambda alpha: 255 if alpha > 32 else 0).getbbox()
    body_w, body_h = x1 - x0, y1 - y0
    front_rim = round(y0 + .30 * body_h)
    bud_size = round(body_w * .37), round(body_h * .72)
    buds = [ImageOps.contain(image, bud_size, Image.Resampling.LANCZOS) for image in (left, right)]

    # Continue the real recess shading from above the seated buds into the
    # area they obscure. Keep the original outer edge and hinge silhouette.
    original = np.asarray(backdrop)
    solid = (original[..., 3] > 128).astype(np.uint8)
    border_distance = cv2.distanceTransform(solid, cv2.DIST_L2, 5)[:front_rim]
    top_sample = round(y0 + .081 * body_h)
    recess = original[top_sample - 2:top_sample + 3, :, :3].mean(axis=0).astype(np.float32)
    recess = cv2.GaussianBlur(recess[None, ...], (0, 0), 2.4)
    level = (np.arange(front_rim, dtype=np.float32)[:, None] - y0) / body_h
    fill = STUDIO["smoothstep"](level, .082, .117) * STUDIO["smoothstep"](border_distance, 2.5, 8)
    cavity = recess - 12 * STUDIO["smoothstep"](level, .125, .30)[..., None]
    lid_pixels = original.copy()
    lid_pixels[:front_rim, :, :3] = np.clip(
        np.rint(original[:front_rim, :, :3] * (1 - fill[..., None]) + cavity * fill[..., None]), 0, 255)
    lid = Image.fromarray(lid_pixels).crop((0, 0, width, front_rim))
    seated = backdrop.crop((0, 0, width, front_rim))
    front = backdrop.crop((0, front_rim, width, height))

    def opacity(image, amount):
        piece = image.copy()
        piece.putalpha(image.getchannel("A").point(lambda alpha: round(alpha * amount)))
        return piece

    def frames():
        for frame in range(76):
            time = frame / SAMPLE_RATE
            clearing = ease(time / .095)
            drop = round(max(0, height - y1 - 10) * ease((time - .055) / .75))
            case_alpha = 1 - ease((time - .29) / .59)
            bud_alpha = clearing * (1 - ease((time - .92) / .33))
            canvas = Image.new("RGBA", backdrop.size)
            canvas.alpha_composite(opacity(Image.blend(seated, lid, clearing), case_alpha), (0, drop))
            for side, bud, delay, landing, angle in zip(
                    (-1, 1), buds, (0, .045), (.09, .15), (13, -9)):
                lift = ease((time - .025 - delay) / .72)
                fan = ease((time - .11 - delay) / .76)
                rolled = bud.rotate(angle * fan, Image.Resampling.BICUBIC, expand=True)
                seat_x = x0 + body_w * (.5 + side * .245)
                release_x = width * (.5 + side * .18)
                head_y = y0 + body_h * .111
                x = round(seat_x + (release_x - seat_x) * fan - rolled.width / 2)
                y = round(head_y + (height * landing - head_y) * lift + (bud.height - rolled.height) / 2)
                canvas.alpha_composite(opacity(rolled, bud_alpha), (x, y))
            # The actual case rim occludes the stems as they leave their seats.
            canvas.alpha_composite(opacity(front, case_alpha), (0, front_rim + drop))
            yield canvas.tobytes()

    STUDIO["write_clip"](ffmpeg, destination, backdrop.size, frames())


def prepare(ffmpeg, output, spec, archive):
    slug, _, _, layout = spec
    with ZipFile(archive) as bundle, TemporaryDirectory(dir=output) as temporary:
        files = source_members(bundle)
        directory = Path(temporary)
        def clip(name):
            leaf = "ProxCard_" + name + ".mov"
            result = directory / leaf
            result.write_bytes(bundle.read(files[leaf]))
            return result

        motion = {}
        success = None
        cue = None
        photos = {}
        if layout == "split":
            photos = {
                "left": "ProxCard_L@3x.png", "right": "ProxCard_R@3x.png",
                "case": "ProxCard_case-closed-charged@3x.png" if "ProxCard_case-closed-charged@3x.png" in files
                        else "ProxCard_case-closed@3x.png",
                "caseCharging": "ProxCard_case-closed-charging@3x.png",
            }
        artwork = {}
        poses = (inward_buds(ffmpeg, clip("loop-charged"), INWARD_BUD_POSE[slug])
                 if slug in INWARD_BUD_POSE else {})
        for part, leaf in photos.items():
            image = poses.get(part)
            if image is None and leaf in files:
                image = Image.open(BytesIO(bundle.read(files[leaf]))).convert("RGBA")
            if image is not None:
                target = directory / f"local-{slug}-{part}.webp"
                save_photo(studio_photo(image, slug), target)
                artwork[part] = target.name

        opening = clip("connect-engage")
        turn = clip("connect-loop" if "ProxCard_connect-loop.mov" in files else "loop")
        portrait = layout == "single"
        size = HEADPHONE_SIZE if portrait else SIZE
        feature = clip("features") if not portrait and "ProxCard_features.mov" in files else None
        duration = 1.14
        turn_frames = samples(ffmpeg, turn, 12)
        reveal_frames = samples(ffmpeg, feature, duration) if feature else None
        # The headset film begins with a wider, tilted pose. Start the turn
        # after the original camera has finished pulling back.
        start = 40 if portrait else UPRIGHT_START
        cut, loop_start, crop = cut_and_frame(samples(ffmpeg, opening, 3.6), turn_frames, reveal_frames,
                                              start, include_setup=portrait)
        stage = fixed_canvas(crop, size)
        opening_stage = samples(ffmpeg, opening, cut / SAMPLE_RATE, stage)
        camera = waiting_camera(samples(ffmpeg, turn, 12, stage),
                                opening_stage[start:], size, portrait)
        motion = {key: f"local-{slug}-{key}.{ext}"
                  for key, ext in (("intro", "webm"), ("loop", "webm"), ("poster", "webp"))}
        read_frames, write_clip = STUDIO["read_frames"], STUDIO["write_clip"]
        with closing(read_frames(ffmpeg, turn, size, stage, start=loop_start)) as tail, \
                closing(read_frames(ffmpeg, turn, size, stage, end=loop_start)) as head:
            turn_source = chain(tail, head) if loop_start else tail
            write_clip(ffmpeg, directory / motion["loop"], size,
                       (studio_frame(frame, slug, camera) for frame in turn_source))
        with closing(read_frames(ffmpeg, opening, size, stage, start=start, end=cut)) as opening_source:
            poster = studio_frame(next(opening_source), slug, camera)
            save_photo(Image.fromarray(poster), directory / motion["poster"], crop=False)
            frames = (studio_frame(frame, slug, camera) for frame in opening_source)
            write_clip(ffmpeg, directory / motion["intro"], size, chain((poster,), frames))
        if portrait:
            with closing(read_frames(ffmpeg, turn, size, stage, end=1)) as first:
                rest = studio_frame(next(first), slug, camera)
                target = directory / f"local-{slug}-headphone.webp"
                save_photo(Image.fromarray(rest), target)
                artwork["headphone"] = target.name
        elif feature:
            success = f"local-{slug}-success.webm"
            differences = ((silhouette(turn_frames) - silhouette(reveal_frames[0])) ** 2).mean(axis=(1, 2, 3))
            nearest = int(np.argmin(differences))
            if differences[nearest] < .003:
                cue = round((nearest - loop_start) % len(turn_frames) / SAMPLE_RATE, 3)
            cameras = reveal_cameras(samples(ffmpeg, feature, duration, stage), camera)
            with closing(read_frames(ffmpeg, feature, SIZE, stage, end=round(duration * SAMPLE_RATE))) as reveal_source:
                write_clip(ffmpeg, directory / success, SIZE,
                           reveal_frames_on_stage(reveal_source, slug, cameras, duration))
        else:
            success = f"local-{slug}-success.webm"
            cue = round((CLASSIC_FRONT - loop_start) % len(turn_frames) / SAMPLE_RATE, 3)
            with closing(read_frames(ffmpeg, turn, SIZE, stage, start=CLASSIC_FRONT, end=CLASSIC_FRONT + 1)) as open_frame, \
                    Image.open(directory / artwork["left"]) as left, Image.open(directory / artwork["right"]) as right:
                backdrop = Image.fromarray(studio_frame(next(open_frame), slug, camera))
                classic_reveal(ffmpeg, backdrop, left.convert("RGBA"), right.convert("RGBA"), directory / success)

        for filename in (*artwork.values(), *motion.values(), *([success] if success else [])):
            (directory / filename).replace(output / filename)
        return {"art": artwork, "waiting": motion or None, "success": success, "cue": cue}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=Path, default=BUILD / "airpods-popup-models" / "cache")
    parser.add_argument("--output", type=Path, default=BUILD / "airpods-overview")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--model", action="append", choices=SOURCE["ARTWORK_MODELS"], help="Rebuild a subset of artwork profiles")
    args = parser.parse_args()
    archives = {}
    for archive in args.cache.glob("*.zip"):
        with ZipFile(archive) as bundle:
            properties = plistlib.loads(bundle.read("Info.plist"))["MobileAssetProperties"]
        archives[(properties["ProductType"], properties["VariantName"])] = archive
    args.output.mkdir(parents=True, exist_ok=True)
    index_path = args.output / "index.json"
    index = json.loads(index_path.read_text(encoding="utf-8")) if args.model and index_path.exists() else {}
    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        jobs = {
            name: pool.submit(prepare, args.ffmpeg, args.output, spec, archives[spec[1:3]])
            for name, spec in SOURCE["ARTWORK_MODELS"].items() if not args.model or name in args.model
        }
        for name, task in jobs.items():
            index[name] = task.result()
            print(f"Prepared {name}", flush=True)
    pending = args.output / "index.json.part"
    pending.write_text(json.dumps(index, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    pending.replace(index_path)


if __name__ == "__main__":
    main()
