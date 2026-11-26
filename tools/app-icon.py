"""Renders Wayfarer Wanderer's app icon from docs/brand/ridgeline.svg.

Writes src/WayfarerWanderer.App/Shell/Assets/wayfarer.ico (16, 20, 24, 32, 40, 48, 64, 128 and 256 px PNG frames)
and wayfarer.png (256 px, for About, the first-start screen and the welcome page). When the website directory
site/static/ is present, also exports site/static/favicon.png (64 px) and site/static/img/icon.png (256 px).

Run from the repository's root: python3 tools/app-icon.py
"""
from pathlib import Path
import shutil
import struct
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
SVG = ROOT / "docs" / "brand" / "ridgeline.svg"
OUT = ROOT / "src" / "WayfarerWanderer.App" / "Shell" / "Assets"
SITE_STATIC = ROOT / "site" / "static"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def render_png(svg: Path, size: int) -> bytes:
    if rsvg := shutil.which("rsvg-convert"):
        result = subprocess.run(
            [rsvg, "-w", str(size), "-h", str(size), "--format=png", str(svg)],
            check=True,
            capture_output=True,
        )
        return result.stdout
    if magick := shutil.which("magick"):
        result = subprocess.run(
            [magick, "-background", "none", str(svg), "-resize", f"{size}x{size}", "png:-"],
            check=True,
            capture_output=True,
        )
        return result.stdout
    raise RuntimeError("No SVG renderer found on PATH (expected rsvg-convert or magick).")


def pack_ico(frames: list[tuple[int, bytes]]) -> bytes:
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = []
    payload = []
    for size, png in frames:
        dim = 0 if size >= 256 else size
        entries.append(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset))
        payload.append(png)
        offset += len(png)
    return header + b"".join(entries) + b"".join(payload)


def main() -> int:
    if not SVG.is_file():
        print(f"Missing SVG master: {SVG}", file=sys.stderr)
        return 1

    OUT.mkdir(parents=True, exist_ok=True)
    frames = [(size, render_png(SVG, size)) for size in ICO_SIZES]
    by_size = dict(frames)

    (OUT / "wayfarer.png").write_bytes(by_size[256])
    (OUT / "wayfarer.ico").write_bytes(pack_ico(frames))

    if SITE_STATIC.is_dir():
        (SITE_STATIC / "favicon.png").write_bytes(by_size[64])
        img_dir = SITE_STATIC / "img"
        img_dir.mkdir(parents=True, exist_ok=True)
        (img_dir / "icon.png").write_bytes(by_size[256])

    return 0


if __name__ == "__main__":
    sys.exit(main())
