"""Pack Blender's transparent renders into the atlas read by pro3Art.ts."""
import sys
from pathlib import Path

from PIL import Image

source, output = map(Path, sys.argv[1:])
width, height = 384, 212
cols, frames = 5, 20
rows_per_pose = frames // cols
atlas = Image.new("RGBA", (width * cols, height * rows_per_pose * 4))
for pose in range(4):
    for frame in range(frames):
        with Image.open(source / f"{pose}-{frame:02}.png") as image:
            if image.size != (width, height):
                raise ValueError(f"Unexpected frame size in {image.filename}")
            position = (frame % cols * width, (pose * rows_per_pose + frame // cols) * height)
            atlas.paste(image, position)
atlas.save(output, "WEBP", quality=88, alpha_quality=95, method=6)
