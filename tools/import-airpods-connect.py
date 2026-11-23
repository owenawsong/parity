"""Prepare opt-in AirPods connection reveals from the Apple device bundles."""

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import json
from pathlib import Path
import plistlib
import runpy
from tempfile import TemporaryDirectory
from zipfile import BadZipFile, ZipFile

from PIL import Image


IMPORTER = runpy.run_path(str(Path(__file__).with_name("import-airpods-popup-models.py")))
BUILD = IMPORTER["BUILD"]
MODELS = {name: spec for name, spec in IMPORTER["MODELS"].items() if spec[0] != "pro3"}
SAMPLE_SIZE = 160
CANVAS = "400:224"
SIGNAL = bytes(255 if level >= 8 else 0 for level in range(256))


def alpha_bounds(ffmpeg, inputs):
    samples = IMPORTER["run_ffmpeg"](
        ffmpeg, *inputs, "-vf", f"scale={SAMPLE_SIZE}:{SAMPLE_SIZE}:flags=area,format=rgba,alphaextract",
        "-c:v", "rawvideo", "-threads", "1", "-f", "rawvideo", "pipe:1",
    )
    size = SAMPLE_SIZE * SAMPLE_SIZE
    if not samples or len(samples) % size:
        raise ValueError("No complete frames in the selected segment")
    bounds = []
    for offset in range(0, len(samples), size):
        alpha = Image.frombytes("L", (SAMPLE_SIZE, SAMPLE_SIZE), samples[offset:offset + size])
        low, high = alpha.getextrema()
        if low != 0 or high < 192:
            raise ValueError("The source lacks a usable alpha channel. Use FFmpeg 9 or newer")
        bounds.append(alpha.point(SIGNAL).getbbox())
    margin = 5
    return (
        max(0, min(box[0] for box in bounds) - margin),
        max(0, min(box[1] for box in bounds) - margin),
        min(SAMPLE_SIZE, max(box[2] for box in bounds) + margin),
        min(SAMPLE_SIZE, max(box[3] for box in bounds) + margin),
    )


def export_reveal(ffmpeg, source, target, start, duration):
    inputs = ("-threads", "2", "-ss", str(start), "-i", str(source), "-an", "-t", str(duration))
    left, top, right, bottom = alpha_bounds(ffmpeg, inputs)
    near_x = f"floor(iw*{left}/{SAMPLE_SIZE}/2)*2"
    near_y = f"floor(ih*{top}/{SAMPLE_SIZE}/2)*2"
    far_x = f"ceil(iw*{right}/{SAMPLE_SIZE}/2)*2"
    far_y = f"ceil(ih*{bottom}/{SAMPLE_SIZE}/2)*2"
    filters = ",".join((
        f"crop={far_x}-{near_x}:{far_y}-{near_y}:{near_x}:{near_y}",
        f"scale={CANVAS}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos",
        "format=yuva420p",
        f"pad={CANVAS}:(ow-iw)/2:(oh-ih)/2:color=black@0",
        "fps=60",
        f"fade=t=out:st={duration - 0.18:.3f}:d=0.18:alpha=1",
    ))
    IMPORTER["run_ffmpeg"](
        ffmpeg, *inputs, "-vf", filters, "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p", "-threads", "2",
        "-b:v", "0", "-crf", "22", "-deadline", "good", "-cpu-used", "5", "-row-mt", "1", str(target),
    )


def import_model(ffmpeg, output, cache, spec, asset):
    slug, product, variant, layout = spec
    filename = f"{slug}-connect.webm"
    with ZipFile(IMPORTER["get_bundle"](asset, cache)) as bundle, TemporaryDirectory(dir=output) as work:
        properties = plistlib.loads(bundle.read("Info.plist"))["MobileAssetProperties"]
        if (properties.get("ProductType"), properties.get("VariantName")) != (product, variant):
            raise ValueError("The bundle does not match the requested model")
        paths = {Path(path).name: path for path in bundle.namelist() if ".devicebundle/" in path}
        alternatives = ("ProxCard_connect-engage.mov", "ProxCard_loop-charged.mov", "ProxCard_loop.mov")
        member = next((name for name in alternatives if name in paths), None)
        if member is None:
            raise ValueError("The device bundle contains no matching clip")
        stage = Path(work)
        source = stage / member
        source.write_bytes(bundle.read(paths[member]))
        # Cases settle into the open front pose just past 1.4 s. The Max models tilt upright on a shorter timeline.
        start = 0.36 if layout == "split" and member == alternatives[0] else 0.02
        export_reveal(ffmpeg, source, stage / filename, start, 1.22)
        (stage / filename).replace(output / filename)
    return filename


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=BUILD / "pro3-reference" / "catalog.xml")
    parser.add_argument("--cache", type=Path, default=BUILD / "airpods-popup-models" / "cache")
    parser.add_argument("--output", type=Path, default=BUILD / "airpods-connect")
    parser.add_argument("--ffmpeg", default="ffmpeg", help="FFmpeg 9 or newer with Apple HEVC alpha support")
    parser.add_argument("--jobs", type=int, default=4)
    args = parser.parse_args()
    try:
        catalog = plistlib.loads(args.catalog.read_bytes())["Assets"]
    except (OSError, ValueError, KeyError) as error:
        parser.error(f"Cannot load Apple's asset catalog: {error}")
    assets = {(asset.get("ProductType"), asset.get("VariantName")): asset for asset in catalog}
    args.cache.mkdir(parents=True, exist_ok=True)
    args.output.mkdir(parents=True, exist_ok=True)
    index = {}
    with ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
        jobs = {}
        for name, spec in MODELS.items():
            if (asset := assets.get((spec[1], spec[2]))) is None:
                print(f"Skipped {name}: missing asset in the catalog", flush=True)
            else:
                jobs[pool.submit(import_model, args.ffmpeg, args.output, args.cache, spec, asset)] = name
        for job in as_completed(jobs):
            name = jobs[job]
            try:
                index[name] = job.result()
            except (ValueError, OSError, KeyError, BadZipFile) as error:
                print(f"Skipped {name}: {error}", flush=True)
            else:
                print(f"Imported {name}", flush=True)
    if not index:
        return 1
    temporary = args.output / "index.json.part"
    temporary.write_text(json.dumps(index, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    temporary.replace(args.output / "index.json")
    return 0 if len(index) == len(MODELS) else 1


if __name__ == "__main__":
    raise SystemExit(main())
