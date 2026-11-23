"""Build the optional local Pro 3 atlas from Apple's original device asset ZIP."""
import argparse
from io import BytesIO
from pathlib import Path
import plistlib
import subprocess
from tempfile import TemporaryDirectory
from zipfile import ZipFile

from PIL import Image, ImageOps

WIDTH, HEIGHT = 384, 212
COLUMNS, FRAMES = 5, 78
VIDEO_SIZE = (270, 300)
OUTPUT = Path(__file__).resolve().parents[1] / ".build" / "pro3-apple-reveal.webp"
POPUP_VIDEO = OUTPUT.with_name("pro3-popup.webm")
POPUP_POSTER = OUTPUT.with_name("pro3-popup.webp")
CHARGING_VIDEO = OUTPUT.with_name("pro3-popup-charging.webm")
CHARGING_POSTER = OUTPUT.with_name("pro3-popup-charging.webp")


def bundle_file(bundle, suffix):
    paths = [name for name in bundle.namelist() if name.endswith(f".devicebundle/{suffix}")]
    if len(paths) != 1:
        raise ValueError(f"Expected one {suffix} in the Pro 3 bundle")
    return bundle.read(paths[0])


def load_png(bundle, filename):
    with Image.open(BytesIO(bundle_file(bundle, filename))) as image:
        image = image.convert("RGBA")
        return image.crop(image.getbbox(alpha_only=True))


def export_hardware(bundle, output):
    output.mkdir(parents=True, exist_ok=True)
    for name, filename in {
        "pro3-left": "ProxCard_L@3x.png",
        "pro3-right": "ProxCard_R@3x.png",
        "pro3-case": "ProxCard_case-closed-charged@3x.png",
        "pro3-case-charging": "ProxCard_case-closed-charging@3x.png",
    }.items():
        load_png(bundle, filename).save(output / f"{name}.webp", "WEBP", quality=94, alpha_quality=100)


def decode_lid(bundle, ffmpeg):
    with TemporaryDirectory() as temporary:
        video = Path(temporary) / "lid.mov"
        video.write_bytes(bundle_file(bundle, "ProxCard_connect-engage.mov"))
        result = subprocess.run([
            ffmpeg, "-hide_banner", "-v", "error", "-ss", "0.43", "-i", str(video),
            "-vf", "fps=60,scale=270:300:flags=lanczos", "-frames:v", str(FRAMES),
            "-an", "-pix_fmt", "rgba", "-f", "rawvideo", "pipe:1",
        ], capture_output=True)
    if result.returncode != 0:
        raise ValueError("FFmpeg could not decode Apple's HEVC with alpha. "
                         f"Use FFmpeg 9.0 or newer.\n{result.stderr.decode(errors='replace')}")
    frame_bytes = VIDEO_SIZE[0] * VIDEO_SIZE[1] * 4
    if len(result.stdout) != FRAMES * frame_bytes:
        raise ValueError("The source clip did not produce the expected number of frames")
    frames = [
        Image.frombytes("RGBA", VIDEO_SIZE, result.stdout[offset:offset + frame_bytes])
        for offset in range(0, len(result.stdout), frame_bytes)
    ]
    if any(frame.getchannel("A").getextrema() != (0, 255) for frame in frames):
        raise ValueError("The decoded frames must retain Apple's alpha channel")
    boxes = [frame.getbbox(alpha_only=True) for frame in frames]
    bounds = (min(b[0] for b in boxes), min(b[1] for b in boxes),
              max(b[2] for b in boxes), max(b[3] for b in boxes))
    return [ImageOps.contain(frame.crop(bounds), (252, 202), Image.Resampling.LANCZOS)
            for frame in frames]


def split_view(closed, left, right, seated):
    frame = Image.new("RGBA", (WIDTH, HEIGHT))
    frame.alpha_composite(closed, ((WIDTH - closed.width) // 2, (HEIGHT - closed.height) // 2))
    if not seated & 1:
        frame.alpha_composite(left, (16, (HEIGHT - left.height) // 2))
    if not seated & 2:
        frame.alpha_composite(right, (WIDTH - right.width - 16, (HEIGHT - right.height) // 2))
    return frame


def render_popup(bundle, ffmpeg, clip, video, poster_path):
    """Keep Apple's original six-second turn with both the buds and case visible."""
    with TemporaryDirectory(dir=OUTPUT.parent) as temporary:
        stage = Path(temporary)
        source = stage / "popup.mov"
        source.write_bytes(bundle_file(bundle, clip))
        crop = "crop=940:440:54:322,scale=470:220:flags=lanczos"
        command = [ffmpeg, "-hide_banner", "-v", "error", "-i", str(source), "-vf", crop, "-an"]
        poster_data = subprocess.run([
            *command, "-frames:v", "1", "-pix_fmt", "rgba", "-c:v", "png", "-f", "image2pipe", "pipe:1",
        ], capture_output=True, check=True).stdout
        with Image.open(BytesIO(poster_data)) as poster:
            if poster.mode != "RGBA" or poster.getchannel("A").getextrema() != (0, 255):
                raise ValueError("The popup clip must retain Apple's alpha channel")
            poster.save(stage / poster_path.name, "WEBP", quality=94, alpha_quality=100)
        subprocess.run([
            *command, "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p",
            "-b:v", "0", "-crf", "20", "-deadline", "good", "-cpu-used", "4",
            "-row-mt", "1", str(stage / video.name),
        ], capture_output=True, check=True)
        (stage / video.name).replace(video)
        (stage / poster_path.name).replace(poster_path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    args = parser.parse_args()

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    with ZipFile(args.bundle) as bundle:
        properties = plistlib.loads(bundle.read("Info.plist"))["MobileAssetProperties"]
        if properties.get("ProductType") != "AirPodsPro1,3-v2" or properties.get("VariantName") != "White":
            raise ValueError("Use Apple's white AirPods Pro 3 (AirPodsPro1,3-v2) bundle")
        export_hardware(bundle, OUTPUT.parent / "pro3-hardware")
        closed = ImageOps.contain(load_png(bundle, "ProxCard_case-closed-charged@3x.png"), (204, 165))
        left = ImageOps.contain(load_png(bundle, "ProxCard_L@3x.png"), (67, 109))
        right = ImageOps.contain(load_png(bundle, "ProxCard_R@3x.png"), (67, 109))
        frames = decode_lid(bundle, args.ffmpeg)
        render_popup(bundle, args.ffmpeg, "ProxCard_loop-charged.mov", POPUP_VIDEO, POPUP_POSTER)
        render_popup(bundle, args.ffmpeg, "ProxCard_loop-charging.mov", CHARGING_VIDEO, CHARGING_POSTER)

    # The first row holds the three split poses. The reveal starts on row two.
    rows = 1 + (FRAMES + COLUMNS - 1) // COLUMNS
    atlas = Image.new("RGBA", (WIDTH * COLUMNS, HEIGHT * rows))
    for seated in range(3):
        atlas.paste(split_view(closed, left, right, seated), (seated * WIDTH, 0))
    for index, frame in enumerate(frames):
        x = index % COLUMNS * WIDTH + (WIDTH - frame.width) // 2
        y = (1 + index // COLUMNS) * HEIGHT + (HEIGHT - frame.height) // 2
        atlas.paste(frame, (x, y))
    atlas.save(OUTPUT, "WEBP", quality=92, alpha_quality=100, method=6)
    for asset in (OUTPUT, POPUP_VIDEO, POPUP_POSTER, CHARGING_VIDEO, CHARGING_POSTER):
        print(asset)


if __name__ == "__main__":
    main()
