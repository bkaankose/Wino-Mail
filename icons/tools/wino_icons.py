"""Shared helpers for the Wino icon font tools.

Every SVG in icons/svg lives in "em space": viewBox 0 0 <upm> <upm>, y pointing down,
y = 0 on the font's ascent line. A font glyph point (x, y) maps to SVG (x, ascent - y).
"""
from __future__ import annotations

import json
from pathlib import Path

from fontTools.pens.cu2quPen import Cu2QuPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.svgLib.path import SVGPath

ICONS_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = ICONS_DIR.parent
SVG_DIR = ICONS_DIR / "svg"
MANIFEST_PATH = ICONS_DIR / "manifest.json"
ASSETS_DIR = REPO_ROOT / "src" / "Wino.Mail.WinUI" / "Assets"


def load_manifest() -> dict:
    with MANIFEST_PATH.open(encoding="utf-8") as f:
        return json.load(f)


def save_manifest(manifest: dict) -> None:
    text = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    MANIFEST_PATH.write_text(text, encoding="utf-8", newline="\n")


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


def _ntos(value: float) -> str:
    # Keep exact halves from implied on-curve points, trim everything else to 2 decimals.
    text = f"{value:.2f}".rstrip("0").rstrip(".")
    return "0" if text in ("-0", "") else text
