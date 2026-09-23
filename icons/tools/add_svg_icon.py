"""Add (or replace) an icon from any SVG file or path data, e.g. brand logos Fluent does not ship.

    python icons/tools/add_svg_icon.py <Name> --svg logo.svg
    python icons/tools/add_svg_icon.py <Name> --path "F1 M 0 0 L ..."   # XAML geometry or SVG path data
                                      [--codepoint E0xx] [--color #RRGGBB]

The outline is fitted into the same 20-of-24 box Fluent icons use and centered on the em, so it
matches the optical size of the rest of the font. --color makes it a fixed-color glyph (brand
logo) in every font; otherwise it follows Foreground like any other icon.
Run build_fonts.py afterwards.
"""
from __future__ import annotations

import argparse
import re
import xml.etree.ElementTree as ET
from pathlib import Path

import pathops
from fontTools.pens.boundsPen import BoundsPen
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path.parser import parse_path

from add_fluent_icon import ENTRY_KEY_ORDER, SVG_NS, find_or_split_entry, next_free_codepoint
from wino_icons import SVG_DIR, _ntos, format_codepoint, load_manifest, save_manifest, write_em_svg

# Fluent 24 icons keep a 2px margin, so artwork spans at most 20/24 of the em.
CONTENT_RATIO = 20 / 24


def load_paths(svg: Path | None, path_data: str | None) -> pathops.Path:
    sources: list[tuple[str, bool]] = []
    if path_data:
        # XAML geometry prefixes the fill rule: F0 = even-odd, F1 = nonzero.
        evenodd = path_data.lstrip().startswith("F0")
        sources.append((re.sub(r"^\s*F[01]\s*", "", path_data), evenodd))
    if svg:
        for element in ET.parse(svg).getroot().iter(f"{SVG_NS}path"):
            sources.append((element.get("d"), element.get("fill-rule") == "evenodd"))
    combined = pathops.Path()
    for d, evenodd in sources:
        path = pathops.Path()
        parse_path(d, path.getPen())
        if evenodd:
            path.fillType = pathops.FillType.EVEN_ODD
        combined = pathops.op(combined, pathops.simplify(path, fix_winding=True), pathops.PathOp.UNION, fix_winding=True)
    return combined


def fitted_path_data(path: pathops.Path, upm: int) -> str:
    bounds = BoundsPen(None)
    path.draw(bounds)
    x0, y0, x1, y1 = bounds.bounds
    scale = upm * CONTENT_RATIO / max(x1 - x0, y1 - y0)
    dx = upm / 2 - (x0 + x1) / 2 * scale
    dy = upm / 2 - (y0 + y1) / 2 * scale
    pen = SVGPathPen(None, ntos=_ntos)
    path.draw(TransformPen(pen, (scale, 0, 0, scale, dx, dy)))
    return pen.getCommands()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("name")
    parser.add_argument("--svg", type=Path)
    parser.add_argument("--path", dest="path_data")
    parser.add_argument("--codepoint")
    parser.add_argument("--color", help="fixed #RRGGBB color for brand logos")
    parser.add_argument("--source-note", default="custom", help="provenance recorded in the manifest")
    args = parser.parse_args()
    if not (args.svg or args.path_data):
        parser.error("pass --svg or --path")

    manifest = load_manifest()
    upm = manifest["unitsPerEm"]
    write_em_svg(SVG_DIR / f"{args.name}.svg", fitted_path_data(load_paths(args.svg, args.path_data), upm), upm)

    entry = find_or_split_entry(manifest, args.name)
    if args.codepoint:
        entry["codepoint"] = format_codepoint(int(args.codepoint, 16))
    elif "codepoint" not in entry:
        entry["codepoint"] = format_codepoint(next_free_codepoint(manifest))
    entry["svg"] = f"{args.name}.svg"
    entry["source"] = args.source_note
    if "accent" in entry:
        (SVG_DIR / entry.pop("accent")["svg"]).unlink(missing_ok=True)
    if args.color:
        entry["color"] = args.color.upper()
    ordered = {k: entry[k] for k in ENTRY_KEY_ORDER if k in entry}
    entry.clear()
    entry.update(ordered)
    save_manifest(manifest)
    print(f"{args.name} -> U+{entry['codepoint']}")


if __name__ == "__main__":
    main()
