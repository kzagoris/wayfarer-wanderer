"""Draws Wayfarer Wanderer's placeholder mark: a compass needle on a rounded square, in the app's deep teal.

Writes src/WayfarerWanderer.App/Shell/Assets/wayfarer.ico (every size Windows draws) and wayfarer.png (256 px, for
About and the welcome page). Run from the repository's root: python tools/app-icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

OUT = Path("src/WayfarerWanderer.App/Shell/Assets")
SIZE = 1024


def mark(size: int = SIZE) -> Image.Image:
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    s = size / 1024
    draw.rounded_rectangle((40 * s, 40 * s, 984 * s, 984 * s), radius=220 * s, fill=(14, 92, 99, 255))
    # The compass's ring.
    draw.ellipse((200 * s, 200 * s, 824 * s, 824 * s), outline=(226, 240, 238, 255), width=round(44 * s))
    # The needle: north light, south amber, pointing up and to the right, a wanderer's heading.
    centre = (512 * s, 512 * s)
    north, south = (700 * s, 324 * s), (324 * s, 700 * s)
    left, right = (452 * s, 452 * s), (572 * s, 572 * s)
    draw.polygon([north, left, centre, right], fill=(246, 251, 250, 255))
    draw.polygon([south, left, centre, right], fill=(240, 170, 60, 255))
    draw.ellipse((482 * s, 482 * s, 542 * s, 542 * s), fill=(14, 92, 99, 255))
    return image


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    big = mark()
    big.resize((256, 256), Image.LANCZOS).save(OUT / "wayfarer.png")
    big.save(OUT / "wayfarer.ico", sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])


if __name__ == "__main__":
    main()
