"""Pack the original Blender hardware renders for the Parity desktop package."""
from pathlib import Path
import sys

from PIL import Image

source, output = map(Path, sys.argv[1:])
output.mkdir(parents=True, exist_ok=True)
parts = ("headphone",) if (source / "headphone.png").is_file() else ("left", "right", "case")
if (source / "case-charging.png").is_file():
    parts += ("case-charging",)
for part in parts:
    with Image.open(source / (part + ".png")) as image:
        rgba = image.convert("RGBA")
        bounds = rgba.getchannel("A").point(lambda value: 255 if value >= 4 else 0).getbbox()
        if bounds is None:
            raise ValueError(f"The {part} render is empty")
        rgba.crop(bounds).save(output / (part + ".webp"), quality=94, alpha_quality=100, method=6)
