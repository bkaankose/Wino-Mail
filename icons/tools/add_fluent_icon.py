"""Add (or replace) an icon from Fluent UI System Icons.

    python icons/tools/add_fluent_icon.py <fluent_name> <WinoName> [--accent <paletteKey>]
                                          [--codepoint E0xx] [--alias Name ...] [--no-regular]

<fluent_name> is the Fluent file stem without size/style, e.g. "mail_inbox" for
ic_fluent_mail_inbox_24_regular. The Regular outline becomes the base glyph; with --accent the
Filled variant becomes the tinted accent layer drawn underneath it in the color fonts.
--no-regular uses the Filled variant as the base (for icons that only make sense filled).

SVGs come from the @fluentui/svg-icons package version pinned in manifest.json, downloaded
from jsDelivr into icons/.cache, or from --source <dir> if you have the package locally.
Run build_fonts.py afterwards.
"""
from __future__ import annotations

import argparse
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

import pathops
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.svgLib.path.parser import parse_path

from wino_icons import ICONS_DIR, SVG_DIR, _ntos, format_codepoint, load_manifest, parse_codepoint, save_manifest, write_em_svg

CACHE_DIR = ICONS_DIR / ".cache"
SVG_NS = "{http://www.w3.org/2000/svg}"


def fetch(fluent: dict, stem: str, source: Path | None) -> Path:
    file_name = f"{stem}.svg"
    if source:
        path = source / file_name
        if not path.is_file():
            raise SystemExit(f"{path} not found")
        return path
    cached = CACHE_DIR / fluent["version"] / file_name
    if not cached.is_file():
        cached.parent.mkdir(parents=True, exist_ok=True)
        url = f"https://cdn.jsdelivr.net/npm/{fluent['package']}@{fluent['version']}/icons/{file_name}"
        try:
            with urllib.request.urlopen(url) as response:
                cached.write_bytes(response.read())
        except OSError as error:
            raise SystemExit(f"could not download {url}: {error}")
    return cached


def normalized_path_data(svg_file: Path, upm: int, size: int) -> str:
    """Union all paths (honoring fill-rule), remove overlaps, scale the size grid onto the em."""
    root = ET.parse(svg_file).getroot()
    if root.find(f".//{SVG_NS}g[@transform]") is not None or root.find(f".//{SVG_NS}path[@transform]") is not None:
        raise SystemExit(f"{svg_file.name}: transforms are not supported")
    combined = pathops.Path()
    for element in root.iter(f"{SVG_NS}path"):
        path = pathops.Path()
        parse_path(element.get("d"), path.getPen())
        if element.get("fill-rule") == "evenodd":
            path.fillType = pathops.FillType.EVEN_ODD
        path = pathops.simplify(path, fix_winding=True)
        combined = pathops.op(combined, path, pathops.PathOp.UNION, fix_winding=True)
    scale = upm / size
    pen = SVGPathPen(None, ntos=_ntos)
    combined.draw(TransformPen(pen, (scale, 0, 0, scale, 0, 0)))
    return pen.getCommands()


def find_or_split_entry(manifest: dict, name: str) -> dict:
    """Return the entry called <name>. An alias with that name is split off into its own entry
    (new codepoint) so it can get its own outline or accent without changing the original icon."""
    entry = next((i for i in manifest["icons"] if i["name"] == name), None)
    if entry is not None:
        return entry
    for owner in manifest["icons"]:
        if name in owner.get("aliases", []):
            owner["aliases"].remove(name)
            if not owner["aliases"]:
                del owner["aliases"]
            print(f"split alias {name} off {owner['name']}")
            break
    entry = {"name": name}
    manifest["icons"].append(entry)
    return entry


def next_free_codepoint(manifest: dict) -> int:
    used = {parse_codepoint(i["codepoint"]) for i in manifest["icons"] if "codepoint" in i}
    cp = 0xE000
    while cp in used:
        cp += 1
    return cp


ENTRY_KEY_ORDER = ["name", "codepoint", "aliases", "svg", "accent", "color", "layers", "advance", "source"]


def add(manifest: dict, fluent_name: str, name: str, accent: str | None = None, codepoint: str | None = None,
        aliases: list[str] | None = None, no_regular: bool = False, source: Path | None = None) -> dict:
    """Import one Fluent icon into the manifest (not saved) and write its SVGs."""
    fluent = manifest["fluent"]
    upm, size = manifest["unitsPerEm"], fluent["size"]
    base_style = "filled" if no_regular else "regular"

    base_svg = fetch(fluent, f"{fluent_name}_{size}_{base_style}", source)
    write_em_svg(SVG_DIR / f"{name}.svg", normalized_path_data(base_svg, upm, size), upm)

    entry = find_or_split_entry(manifest, name)
    if codepoint:
        entry["codepoint"] = format_codepoint(int(codepoint, 16))
    elif "codepoint" not in entry:
        entry["codepoint"] = format_codepoint(next_free_codepoint(manifest))
    entry["svg"] = f"{name}.svg"
    entry["source"] = f"fluent:{fluent_name}"
    entry.pop("advance", None)
    entry.pop("color", None)
    for old in entry.pop("layers", []):
        (SVG_DIR / old["svg"]).unlink(missing_ok=True)
    if aliases:
        entry["aliases"] = sorted(set(entry.get("aliases", [])) | set(aliases))

    if accent:
        filled_svg = fetch(fluent, f"{fluent_name}_{size}_filled", source)
        accent_file = f"{name}.accent.svg"
        write_em_svg(SVG_DIR / accent_file, normalized_path_data(filled_svg, upm, size), upm)
        entry["accent"] = {"svg": accent_file, "color": accent}
    elif "accent" in entry:
        (SVG_DIR / entry.pop("accent")["svg"]).unlink(missing_ok=True)

    # Stable key order keeps manifest diffs readable.
    ordered = {k: entry[k] for k in ENTRY_KEY_ORDER if k in entry}
    entry.clear()
    entry.update(ordered)

    print(f"{name} -> U+{entry['codepoint']} from {fluent_name}_{size}_{base_style}" + (f" + accent '{accent}'" if accent else ""))
    return entry


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("fluent_name")
    parser.add_argument("name")
    parser.add_argument("--accent", help="palette key for the Filled accent layer")
    parser.add_argument("--codepoint", help="hex codepoint (default: keep existing, else next free)")
    parser.add_argument("--alias", action="append", default=[])
    parser.add_argument("--no-regular", action="store_true", help="use the Filled outline as the base glyph")
    parser.add_argument("--source", type=Path, help="local @fluentui/svg-icons icons directory")
    args = parser.parse_args()

    manifest = load_manifest()
    add(manifest, args.fluent_name, args.name, args.accent, args.codepoint, args.alias, args.no_regular, args.source)
    save_manifest(manifest)


if __name__ == "__main__":
    main()
