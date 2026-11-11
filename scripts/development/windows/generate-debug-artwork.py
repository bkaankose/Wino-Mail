"""Regenerate the amber Debug artwork from the stable Windows branding assets.

Run from the repository root after the stable artwork changes:

    python scripts/development/windows/generate-debug-artwork.py

Requires Pillow. Debug builds package the output instead of the stable files.
"""

import colorsys
import re
import shutil
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / "src" / "Wino.Mail.WinUI"
OUTPUT = Path(__file__).resolve().parent / "DebugAssets"

TARGET_HUE = 41 / 360
TARGET_SATURATION = 0.80
# The stable frame blue (#0051B7) sits at this lightness; it maps onto the lightness of #EAB543.
SOURCE_ANCHOR = 0.36
TARGET_ANCHOR = 0.59
BLUE_HUES = (170 / 360, 275 / 360)


def recolor(r: int, g: int, b: int) -> tuple[int, int, int]:
    h, l, s = colorsys.rgb_to_hls(r / 255, g / 255, b / 255)
    if s < 0.08 or not BLUE_HUES[0] <= h <= BLUE_HUES[1]:
        return r, g, b
    if l <= SOURCE_ANCHOR:
        l = l * TARGET_ANCHOR / SOURCE_ANCHOR
    else:
        l = 1 - (1 - l) * (1 - TARGET_ANCHOR) / (1 - SOURCE_ANCHOR)
    s = min(1.0, s * TARGET_SATURATION)
    nr, ng, nb = colorsys.hls_to_rgb(TARGET_HUE, l, s)
    return round(nr * 255), round(ng * 255), round(nb * 255)


def recolor_image(image: Image.Image) -> Image.Image:
    rgba = image.convert("RGBA")
    cache: dict[tuple[int, int, int], tuple[int, int, int]] = {}
    pixels = rgba.load()
    for y in range(rgba.height):
        for x in range(rgba.width):
            r, g, b, a = pixels[x, y]
            if a == 0:
                continue
            key = (r, g, b)
            mapped = cache.get(key)
            if mapped is None:
                mapped = cache[key] = recolor(r, g, b)
            pixels[x, y] = (*mapped, a)
    return rgba


def recolor_svg(text: str) -> str:
    def replace(match: re.Match[str]) -> str:
        value = match.group(1)
        if len(value) == 3:
            value = "".join(c * 2 for c in value)
        r, g, b = recolor(int(value[0:2], 16), int(value[2:4], 16), int(value[4:6], 16))
        return f"#{r:02X}{g:02X}{b:02X}"

    return re.sub(r"#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b", replace, text)


def recolor_ico(source: Path, destination: Path) -> None:
    icon = Image.open(source)
    frames = []
    for size in sorted(icon.info.get("sizes", {icon.size}), reverse=True):
        icon.size = size
        icon.load()
        frames.append(recolor_image(icon.copy()))
    frames[0].save(destination, format="ICO", sizes=[f.size for f in frames], append_images=frames[1:])


def branding_files() -> list[Path]:
    assets = SOURCE / "Assets"
    files = [p for p in (assets / "AppEntries").rglob("*") if p.suffix in (".png", ".svg") and "_Sources" not in p.parts]
    files += list(assets.glob("StoreLogo*.png"))
    files += [assets / "Wino_Icon.ico", assets / "Wino_Icon_Unread.ico", assets / "EML" / "eml.png"]
    return sorted(p for p in files if p.is_file())


def main() -> int:
    if OUTPUT.exists():
        shutil.rmtree(OUTPUT)
    for source in branding_files():
        destination = OUTPUT / source.relative_to(SOURCE)
        destination.parent.mkdir(parents=True, exist_ok=True)
        if source.suffix == ".svg":
            destination.write_text(recolor_svg(source.read_text(encoding="utf-8")), encoding="utf-8")
        elif source.suffix == ".ico":
            recolor_ico(source, destination)
        else:
            with Image.open(source) as image:
                recolor_image(image).save(destination, format="PNG", optimize=True)
        print(destination.relative_to(ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
