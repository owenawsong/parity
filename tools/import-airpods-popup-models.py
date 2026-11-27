"""Import optional local AirPods popup motion from Apple's SharingDeviceAssets catalog."""

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
from contextlib import closing
from io import BytesIO
from itertools import chain
import json
import math
from pathlib import Path
import plistlib
import runpy
import shutil
import subprocess
from tempfile import TemporaryDirectory
from urllib.parse import urlparse
from urllib.request import urlopen
from zipfile import BadZipFile, ZipFile

from PIL import Image


BUILD = Path(__file__).resolve().parents[1] / ".build"
# The two AirPods 5 packages have identical rotation clips. Core groups the
# Max 1 connectors, so use the Silver Lightning art for that entry.
MODELS = {
    "AirPods Pro 1": ("pro1", "AirPodsPro1,1-v2", "White", "split"),
    "AirPods Pro 2": ("pro2", "AirPodsPro1,2-v2", "White", "split"),
    "AirPods 2": ("airpods2", "AirPods1,2-v2", "White", "split"),
    "AirPods 3": ("airpods3", "AirPods1,3-v2", "White", "split"),
    "AirPods 4": ("airpods4", "AirPods1,4-v2", "White", "split"),
    "AirPods 4 (ANC)": ("airpods4-anc", "AirPods1,5-v2", "White", "split"),
    "AirPods Pro 3": ("pro3", "AirPodsPro1,3-v2", "White", "split"),
    "AirPods 1": ("airpods1", "AirPods1,1-v2", "White", "split"),
    "AirPods 5": ("airpods5", "AirPods3,7-v2", "White", "split"),
    "AirPods Max 1": ("max1", "AirPodsMax1,1-v2", "Silver", "single"),
    # Max 2 and Max 1 USB-C share this asset key.
    "AirPods Max 2": ("max2", "AirPodsMax1,2-v2", "Silver", "single"),
}
MAX_FINISHES = {
    "space-gray": ("max-space-gray", "AirPodsMax1,1-v2", "Dark Grey", "single"),
    "sky-blue": ("max-sky-blue", "AirPodsMax1,1-v2", "Blue", "single"),
    "green": ("max-green", "AirPodsMax1,1-v2", "Green", "single"),
    "pink": ("max-pink", "AirPodsMax1,1-v2", "Red", "single"),
    "midnight": ("max-midnight", "AirPodsMax1,2-v2", "Black", "single"),
    "blue": ("max-blue", "AirPodsMax1,2-v2", "Blue", "single"),
    "purple": ("max-purple", "AirPodsMax1,2-v2", "Purple", "single"),
    "orange": ("max-orange", "AirPodsMax1,2-v2", "Orange", "single"),
}
ARTWORK_MODELS = MODELS | {f"max:{color}": spec for color, spec in MAX_FINISHES.items()}
SAMPLE_SIZE = 128
MIN_ALPHA = bytes(255 if value >= 8 else 0 for value in range(256))
SIZE = 470, 220
CANVAS = f"{SIZE[0]}:{SIZE[1]}"


def run_ffmpeg(ffmpeg, *arguments):
    result = subprocess.run([
        ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-filter_threads", "1", *arguments,
    ], capture_output=True)
    if result.returncode:
        raise ValueError(result.stderr.decode(errors="replace").strip())
    return result.stdout


def get_bundle(asset, cache):
    url = asset["__BaseURL"] + asset["__RelativePath"]
    if urlparse(url).scheme != "https" or urlparse(url).hostname != "updates.cdn-apple.com":
        raise ValueError("The bundle URL is outside Apple's asset CDN")
    bundle = cache / Path(urlparse(url).path).name
    if bundle.is_file() and bundle.stat().st_size == asset.get("_DownloadSize"):
        return bundle
    with TemporaryDirectory(dir=cache) as temporary:
        download = Path(temporary) / bundle.name
        with urlopen(url, timeout=30) as response, download.open("wb") as output:
            shutil.copyfileobj(response, output)
        if asset.get("_DownloadSize") and download.stat().st_size != asset["_DownloadSize"]:
            raise ValueError("Download size differs from the Apple catalog")
        download.replace(bundle)
    return bundle


def alpha_crop(ffmpeg, source):
    # Scan the entire clip so the case and both buds stay in frame during their turn.
    sample = run_ffmpeg(
        ffmpeg, "-threads", "2", "-i", str(source), "-an", "-vf",
        f"scale={SAMPLE_SIZE}:{SAMPLE_SIZE}:flags=area,format=rgba,alphaextract",
        "-c:v", "rawvideo", "-threads", "1", "-f", "rawvideo", "pipe:1",
    )
    frame_size = SAMPLE_SIZE ** 2
    if not sample or len(sample) % frame_size:
        raise ValueError("The clip did not decode into complete frames")
    boxes = []
    for offset in range(0, len(sample), frame_size):
        alpha = Image.frombytes("L", (SAMPLE_SIZE, SAMPLE_SIZE), sample[offset:offset + frame_size])
        low, high = alpha.getextrema()
        if low > 0 or (0 < high < 192):
            raise ValueError("The clip lacks a usable alpha channel. Use FFmpeg 9 or newer")
        if high:
            boxes.append(alpha.point(MIN_ALPHA).getbbox())
    if not boxes or len(boxes) != len(sample) // frame_size:
        raise ValueError("The clip has blank frames and cannot play as a continuous idle loop")
    return (
        min(box[0] for box in boxes), min(box[1] for box in boxes),
        max(box[2] for box in boxes), max(box[3] for box in boxes),
    )


def render_clip(ffmpeg, source, stage, basename, crop, slug, studio):
    video = f"{basename}.webm"
    poster = f"{basename}.webp"
    x, y, right, bottom = crop
    filters = ",".join([
        f"crop=ceil(iw*{right-x}/{SAMPLE_SIZE}/2)*2:ceil(ih*{bottom-y}/{SAMPLE_SIZE}/2)*2:"
        f"floor(iw*{x}/{SAMPLE_SIZE}/2)*2:floor(ih*{y}/{SAMPLE_SIZE}/2)*2",
        f"scale={CANVAS}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos",
        "format=yuva420p",
        f"pad={CANVAS}:(ow-iw)/2:(oh-ih)/2:color=black@0",
    ])
    with closing(studio["read_frames"](ffmpeg, source, SIZE, filters)) as source_frames:
        frames = (studio["light_shell"](frame, slug) for frame in source_frames)
        first = next(frames, None)
        if first is None:
            raise ValueError(f"No usable frames in {source.name}")
        frame = Image.fromarray(first)
        if frame.getchannel("A").getextrema() != (0, 255):
            raise ValueError("The rendered poster lost its alpha channel")
        frame.save(stage / poster, "WEBP", quality=94, alpha_quality=100)
        studio["write_clip"](ffmpeg, stage / video, SIZE, chain((first,), frames))
    rgba = run_ffmpeg(ffmpeg, "-c:v", "libvpx-vp9", "-threads", "2", "-i", str(stage / video),
                      "-frames:v", "1", "-pix_fmt", "rgba", "-c:v", "png", "-f", "image2pipe", "pipe:1")
    with Image.open(BytesIO(rgba)) as frame:
        if frame.getchannel("A").getextrema() != (0, 255):
            raise ValueError("The encoded video lost its alpha channel")
    return {"video": video, "poster": poster}


def import_model(ffmpeg, output, name, spec, asset, studio):
    slug, product, variant, layout = spec
    cache = output / "cache"
    entry = {"layout": layout}
    warnings = []
    with ZipFile(get_bundle(asset, cache)) as bundle, TemporaryDirectory(dir=output) as temporary:
        properties = plistlib.loads(bundle.read("Info.plist"))["MobileAssetProperties"]
        if (properties.get("ProductType"), properties.get("VariantName")) != (product, variant):
            raise ValueError("The downloaded asset does not match the catalog entry")
        stage = Path(temporary)
        files = {Path(path).name: path for path in bundle.namelist()
                 if ".devicebundle/" in path and path.endswith(".mov")}
        choices = {
            "standard": ("ProxCard_loop-charged.mov", "ProxCard_loop.mov"),
            "charging": ("ProxCard_loop-charging.mov",),
        }
        sources = {}
        boxes = []
        for role, alternatives in choices.items():
            match = next((files[filename] for filename in alternatives if filename in files), None)
            if match is None:
                if role == "standard":
                    raise ValueError("Apple's package does not supply an idle rotation")
                continue
            source = stage / Path(match).name
            source.write_bytes(bundle.read(match))
            try:
                boxes.append(alpha_crop(ffmpeg, source))
                sources[role] = source
            except ValueError as error:
                if role == "standard":
                    raise
                warnings.append(f"{name} charging: {error}")

        # Match the standard and charging geometry, with margin for sampling and filtering.
        margin = math.ceil(SAMPLE_SIZE * 0.035)
        crop = (
            max(0, min(box[0] for box in boxes) - margin),
            max(0, min(box[1] for box in boxes) - margin),
            min(SAMPLE_SIZE, max(box[2] for box in boxes) + margin),
            min(SAMPLE_SIZE, max(box[3] for box in boxes) + margin),
        )
        for role, source in sources.items():
            try:
                rendered = render_clip(ffmpeg, source, stage, f"{slug}-{role}", crop, slug, studio)
                for filename in rendered.values():
                    (stage / filename).replace(output / filename)
                entry[role] = rendered
            except ValueError as error:
                if role == "standard":
                    raise
                warnings.append(f"{name} charging: {error}")

    return entry, warnings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=BUILD / "pro3-reference" / "catalog.xml")
    parser.add_argument("--output", type=Path, default=BUILD / "airpods-popup-models")
    parser.add_argument("--ffmpeg", default="ffmpeg", help="FFmpeg 9 or newer with Apple HEVC alpha decoding")
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--model", action="append", choices=ARTWORK_MODELS, help="Import a subset of the artwork profiles")
    args = parser.parse_args()

    try:
        catalog = plistlib.loads(args.catalog.read_bytes())["Assets"]
    except (OSError, ValueError, KeyError) as error:
        parser.error(f"Cannot read the Apple asset catalog: {error}")
    assets = {(asset.get("ProductType"), asset.get("VariantName")): asset for asset in catalog}
    (args.output / "cache").mkdir(parents=True, exist_ok=True)
    index_path = args.output / "index.json"
    index = json.loads(index_path.read_text(encoding="utf-8")) if index_path.exists() else {}
    names = args.model or ARTWORK_MODELS
    imported = 0
    studio = runpy.run_path(str(Path(__file__).with_name("airpods-studio.py")))
    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        jobs = {}
        for name in names:
            spec = ARTWORK_MODELS[name]
            asset = assets.get((spec[1], spec[2]))
            if asset is None:
                print(f"Skipped {name}: {spec[1]} {spec[2]} is absent from the Apple catalog", flush=True)
                continue
            jobs[pool.submit(import_model, args.ffmpeg, args.output, name, spec, asset, studio)] = name

        for job in as_completed(jobs):
            name = jobs[job]
            try:
                entry, warnings = job.result()
            except (ValueError, OSError, KeyError, BadZipFile) as error:
                print(f"Skipped {name}: {error}", flush=True)
                continue
            for warning in warnings:
                print(warning, flush=True)
            index[name] = entry
            temporary = index_path.with_suffix(".json.part")
            temporary.write_text(json.dumps(index, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            temporary.replace(index_path)
            imported += 1
            print(f"Imported {name} ({', '.join(key for key in entry if key != 'layout')})", flush=True)

    print(f"Imported {imported} model(s) to {index_path}", flush=True)
    return 0 if imported else 1


if __name__ == "__main__":
    raise SystemExit(main())
