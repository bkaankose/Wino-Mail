"""Shared helpers for the Wino icon font tools.

Every SVG in icons/svg lives in "em space": viewBox 0 0 <upm> <upm>, y pointing down,
y = 0 on the font's ascent line. A font glyph point (x, y) maps to SVG (x, ascent - y).
"""
from __future__ import annotations

import json
import xml.etree.ElementTree as ET
from pathlib import Path

import pathops
from fontTools.pens.cu2quPen import Cu2QuPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.svgLib.path import SVGPath
from fontTools.svgLib.path.parser import parse_path

ICONS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = ICONS_DIR.parent
SVG_DIR = ICONS_DIR / "svg"
MANIFEST_PATH = ICONS_DIR / "manifest.json"
SETS_PATH = ICONS_DIR / "sets.json"
SETS_DIR = ICONS_DIR / "sets"
SVG_NS = "{http://www.w3.org/2000/svg}"
ASSETS_DIR = REPO_ROOT / "src" / "Wino.Mail.WinUI" / "Assets"


def load_manifest() -> dict:
    with MANIFEST_PATH.open(encoding="utf-8") as f:
        return json.load(f)


def save_manifest(manifest: dict) -> None:
    text = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    MANIFEST_PATH.write_text(text, encoding="utf-8", newline="\n")


def load_sets() -> dict:
    with SETS_PATH.open(encoding="utf-8") as f:
        return json.load(f)


def set_font_specs(set_name: str) -> dict:
    """The three fonts of an icon set. Names must match WinoIconFonts in Wino.Core.Domain."""
    return {
        "mono": {"file": f"WinoIcons{set_name}.ttf", "family": f"WinoIcons{set_name}"},
        "light": {"file": f"WinoIcons{set_name}Color-Light.ttf", "family": f"WinoIcons{set_name}ColorLight", "palette": "light"},
        "dark": {"file": f"WinoIcons{set_name}Color-Dark.ttf", "family": f"WinoIcons{set_name}ColorDark", "palette": "dark"},
    }


def parse_codepoint(value: str) -> int:
    return int(value, 16)


def format_codepoint(cp: int) -> str:
    return f"{cp:04X}"


def em_to_font_transform(ascent: int) -> tuple:
    # SVG em space (y down, 0 = ascent) -> font units (y up).
    return (1, 0, 0, -1, 0, ascent)


def font_to_em_transform(ascent: int) -> tuple:
    return (1, 0, 0, -1, 0, ascent)


def write_em_svg(path: Path, d: str, upm: int) -> None:
    body = f'<path d="{d}"/>' if d else ""
    path.write_text(
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {upm} {upm}">{body}</svg>\n',
        encoding="utf-8",
        newline="\n",
    )


def draw_to_svg_path(draw, ascent: int) -> str:
    """Run draw(pen) against a pen in font units and return em-space SVG path data."""
    pen = SVGPathPen(None, ntos=_ntos)
    draw(TransformPen(pen, font_to_em_transform(ascent)))
    return pen.getCommands()


def svg_to_glyph(svg_path: Path, ascent: int, glyf_table=None):
    """Load an em-space SVG and return a TrueType glyph."""
    pen = TTGlyphPen(glyf_table)
    SVGPath(str(svg_path)).draw(TransformPen(Cu2QuPen(pen, max_err=0.5), em_to_font_transform(ascent)))
    return pen.glyph(dropImpliedOnCurves=True)


def path_to_glyph(d: str, upm: int, ascent: int, glyf_table=None):
    """Return a TrueType glyph for em-space SVG path data (the icon set files store paths, not SVG files)."""
    svg = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {upm} {upm}"><path d="{d}"/></svg>'.encode()
    pen = TTGlyphPen(glyf_table)
    SVGPath.fromstring(svg).draw(TransformPen(Cu2QuPen(pen, max_err=0.5), em_to_font_transform(ascent)))
    return pen.glyph(dropImpliedOnCurves=True)


def _translation(element: ET.Element, label: str) -> tuple[float, float]:
    value = element.get("transform")
    if not value:
        return 0.0, 0.0
    parts = value.strip().removeprefix("translate(").removesuffix(")").replace(",", " ").split()
    if not value.strip().startswith("translate(") or len(parts) not in (1, 2):
        raise SystemExit(f"{label}: only translate() transforms are supported, not '{value}'")
    return float(parts[0]), float(parts[1]) if len(parts) == 2 else 0.0


def _paths(element: ET.Element, offset: tuple[float, float], label: str):
    """Yield (path element, accumulated translation) for every <path> under <element>."""
    dx, dy = _translation(element, label)
    offset = (offset[0] + dx, offset[1] + dy)
    tag = element.tag.removeprefix(SVG_NS)
    if tag in ("circle", "ellipse", "rect", "line", "polyline", "polygon", "use", "text"):
        raise SystemExit(f"{label}: <{tag}> is not supported, only <path>")
    if tag == "path":
        yield element, offset
    for child in element:
        yield from _paths(child, offset, label)


def union_path_data(root: ET.Element, transform: tuple, label: str) -> str:
    """Union every path of a parsed SVG (honoring fill-rule and translate() transforms), remove overlaps and map
    it into em space with <transform>. Only <path> shapes are supported."""
    combined = pathops.Path()
    for element, (dx, dy) in _paths(root, (0.0, 0.0), label):
        path = pathops.Path()
        parse_path(element.get("d"), TransformPen(path.getPen(), (1, 0, 0, 1, dx, dy)))
        if element.get("fill-rule") == "evenodd":
            path.fillType = pathops.FillType.EVEN_ODD
        path = pathops.simplify(path, fix_winding=True)
        combined = pathops.op(combined, path, pathops.PathOp.UNION, fix_winding=True)
    pen = SVGPathPen(None, ntos=_ntos)
    combined.draw(TransformPen(pen, transform))
    return pen.getCommands()


def _ntos(value: float) -> str:
    # Keep exact halves from implied on-curve points, trim everything else to 2 decimals.
    text = f"{value:.2f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text
