"""Build the Wino icon fonts from icons/manifest.json and icons/svg.

    python icons/tools/build_fonts.py                 # write the fonts into Wino.Mail.WinUI/Assets
    python icons/tools/build_fonts.py --check         # exit 1 if the committed fonts are stale
    python icons/tools/build_fonts.py --verify-against old.ttf   # compare outlines with another font
    python icons/tools/build_fonts.py --preview out.html          # glyph sheet for a browser

Three fonts are produced from the same outlines:
  WinoIcons.ttf               monochrome, every glyph follows Foreground
  WinoIconsColor-Light.ttf    COLR v0: accent layer (palette color) under the base layer (foreground)
  WinoIconsColor-Dark.ttf     same layers, dark palette

Glyphs with a fixed "color" (brand logos) are a single colored layer in all three fonts.
Glyphs with fixed-color "layers" (multi-color brand logos) keep those layers in all three fonts.
"""
from __future__ import annotations

import argparse
import html
import io
import sys
from pathlib import Path

from fontTools.colorLib.builder import buildCOLR, buildCPAL
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.statisticsPen import StatisticsPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont

from wino_icons import ASSETS_DIR, REPO_ROOT, SVG_DIR, load_manifest, parse_codepoint, svg_to_glyph

FOREGROUND = 0xFFFF
# Fixed timestamp keeps the output byte-for-byte reproducible (2024-01-01, seconds since 1904).
FIXED_TIMESTAMP = 3786825600


def parse_color(value: str) -> tuple[float, float, float, float]:
    h = value.lstrip("#")
    if len(h) == 6:
        h += "FF"
    r, g, b, a = (int(h[i : i + 2], 16) / 255 for i in range(0, 8, 2))
    return (r, g, b, a)


def validate(manifest: dict) -> None:
    seen_names: set[str] = set()
    seen_codepoints: dict[int, str] = {}
    errors = []
    for icon in manifest["icons"]:
        for name in [icon["name"], *icon.get("aliases", [])]:
            if not name.isidentifier() or name == "None":
                errors.append(f"{name}: not a valid enum member name")
            if name in seen_names:
                errors.append(f"{name}: duplicate name")
            seen_names.add(name)
        cp = parse_codepoint(icon["codepoint"])
        if not (0xE000 <= cp <= 0xF8FF or 0xF0000 <= cp <= 0xFFFFD):
            errors.append(f"{icon['name']}: U+{cp:X} is outside the private use areas")
        if cp in seen_codepoints:
            errors.append(f"{icon['name']}: U+{cp:X} already used by {seen_codepoints[cp]}")
        seen_codepoints[cp] = icon["name"]
        svgs = [icon["svg"], *([icon["accent"]["svg"]] if "accent" in icon else []), *(l["svg"] for l in icon.get("layers", []))]
        for svg in svgs:
            if not (SVG_DIR / svg).is_file():
                errors.append(f"{icon['name']}: missing {svg}")
        if sum(key in icon for key in ("accent", "color", "layers")) > 1:
            errors.append(f"{icon['name']}: accent, color and layers are exclusive")
        accent = icon.get("accent")
        if accent:
            for palette_name, palette in manifest["palettes"].items():
                if accent["color"] not in palette:
                    errors.append(f"{icon['name']}: palette '{palette_name}' has no color '{accent['color']}'")
    if errors:
        raise SystemExit("manifest errors:\n  " + "\n  ".join(errors))


def build_font(manifest: dict, variant: str) -> bytes:
    spec = manifest["fonts"][variant]
    upm = manifest["unitsPerEm"]
    ascent = manifest["ascent"]
    descent = manifest["descent"]
    palette_name = spec.get("palette")

    glyph_order = [".notdef", "space"]
    glyphs = {".notdef": TTGlyphPen(None).glyph(), "space": TTGlyphPen(None).glyph()}
    advances = {".notdef": upm, "space": upm}
    cmap = {0x20: "space"}
    layers: dict[str, list[tuple[str, int]]] = {}
    colors: list[str] = []

    def color_index(value: str) -> int:
        if value not in colors:
            colors.append(value)
        return colors.index(value)

    for icon in manifest["icons"]:
        name = icon["name"]
        glyph_order.append(name)
        glyphs[name] = svg_to_glyph(SVG_DIR / icon["svg"], ascent)
        advances[name] = icon.get("advance", upm)
        cmap[parse_codepoint(icon["codepoint"])] = name

        if "color" in icon:
            layers[name] = [(name, color_index(icon["color"]))]
        elif "layers" in icon:
            layers[name] = []
            for index, layer in enumerate(icon["layers"], 1):
                layer_name = f"{name}.layer{index}"
                glyph_order.append(layer_name)
                glyphs[layer_name] = svg_to_glyph(SVG_DIR / layer["svg"], ascent)
                advances[layer_name] = advances[name]
                layers[name].append((layer_name, color_index(layer["color"])))
        elif palette_name and "accent" in icon:
            accent_name = f"{name}.accent"
            glyph_order.append(accent_name)
            glyphs[accent_name] = svg_to_glyph(SVG_DIR / icon["accent"]["svg"], ascent)
            advances[accent_name] = advances[name]
            palette_color = manifest["palettes"][palette_name][icon["accent"]["color"]]
            layers[name] = [(accent_name, color_index(palette_color)), (name, FOREGROUND)]

    fb = FontBuilder(upm, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap(cmap)
    fb.setupGlyf(glyphs)
    glyf = fb.font["glyf"]
    metrics = {}
    for name in glyph_order:
        g = glyf[name]
        g.recalcBounds(glyf)
        metrics[name] = (advances[name], g.xMin if g.numberOfContours else 0)
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=ascent, descent=-descent)
    family = spec["family"]
    fb.setupNameTable(
        {
            "familyName": family,
            "styleName": "Regular",
            "uniqueFontIdentifier": family,
            "fullName": f"{family} Regular",
            "psName": family,
            "version": "Version 1.0",
        }
    )
    fb.setupOS2(
        sTypoAscender=ascent,
        sTypoDescender=-descent,
        sTypoLineGap=0,
        usWinAscent=ascent,
        usWinDescent=descent,
        achVendID="WINO",
        fsType=0,
    )
    fb.setupPost()
    head = fb.font["head"]
    head.created = head.modified = FIXED_TIMESTAMP

    if layers:
        fb.font["CPAL"] = buildCPAL([[parse_color(c) for c in colors]])
        fb.font["COLR"] = buildCOLR(layers, version=0)

    buffer = io.BytesIO()
    fb.font.save(buffer, reorderTables=True)
    return buffer.getvalue()


def build_all(manifest: dict) -> dict[str, bytes]:
    validate(manifest)
    return {manifest["fonts"][v]["file"]: build_font(manifest, v) for v in manifest["fonts"]}


def output_paths(manifest: dict) -> list[tuple[Path, str]]:
    """Every place a font is written: the app's Assets folder plus any extra copies, such as the
    monochrome font the controls library packages for itself."""
    paths = []
    for spec in manifest["fonts"].values():
        paths.append((ASSETS_DIR / spec["file"], spec["file"]))
        for copy in spec.get("copies", []):
            paths.append((REPO_ROOT / copy / spec["file"], spec["file"]))
    return paths


def outline_stats(glyph_set, name):
    pen = StatisticsPen(glyphset=glyph_set)
    glyph_set[name].draw(pen)
    return pen.area, pen.meanX, pen.meanY


def visible_glyph(font: TTFont, glyph_name: str) -> str:
    # For one-layer color glyphs the layer carries the outline that is actually drawn.
    if "COLR" in font and glyph_name in font["COLR"].ColorLayers:
        layer_list = font["COLR"].ColorLayers[glyph_name]
        if len(layer_list) == 1:
            return layer_list[0].name
    return glyph_name


def verify(new_bytes: bytes, old_path: Path, manifest: dict) -> int:
    old = TTFont(str(old_path))
    new = TTFont(io.BytesIO(new_bytes))
    old_cmap, new_cmap = old.getBestCmap(), new.getBestCmap()
    old_set, new_set = old.getGlyphSet(), new.getGlyphSet()
    wanted = {parse_codepoint(i["codepoint"]) for i in manifest["icons"]}
    failures = 0
    for cp in sorted(wanted):
        if cp not in old_cmap:
            continue
        if cp not in new_cmap:
            print(f"U+{cp:X}: missing from the new font")
            failures += 1
            continue
        a = outline_stats(old_set, visible_glyph(old, old_cmap[cp]))
        b = outline_stats(new_set, visible_glyph(new, new_cmap[cp]))
        area_ok = abs(a[0] - b[0]) <= max(abs(a[0]) * 0.002, 50)
        center_ok = abs(a[1] - b[1]) <= 1 and abs(a[2] - b[2]) <= 1
        if old["hmtx"][old_cmap[cp]][0] != new["hmtx"][new_cmap[cp]][0]:
            print(f"U+{cp:X}: advance differs")
            failures += 1
        if not (area_ok and center_ok):
            print(f"U+{cp:X} {new_cmap[cp]}: outline differs (area {a[0]:.0f} vs {b[0]:.0f}, center {a[1]:.1f},{a[2]:.1f} vs {b[1]:.1f},{b[2]:.1f})")
            failures += 1
    checked = len([cp for cp in wanted if cp in old_cmap])
    print(f"verified {checked} glyphs against {old_path.name}: {failures} difference(s)")
    return failures


def write_preview(manifest: dict, fonts: dict[str, bytes], out: Path) -> None:
    # Fonts are copied next to the page so it works from any folder (and any drive).
    out.parent.mkdir(parents=True, exist_ok=True)
    faces, sections = [], []
    for variant, spec in manifest["fonts"].items():
        (out.parent / spec["file"]).write_bytes(fonts[spec["file"]])
        faces.append(f"@font-face{{font-family:'{spec['family']}';src:url('{spec['file']}')}}")
    cells = []
    for icon in manifest["icons"]:
        ch = f"&#x{icon['codepoint']};"
        tag = " accent" if "accent" in icon or "layers" in icon else ""
        cells.append(f'<div class="cell{tag}"><span class="g">{ch}</span><span class="n">{html.escape(icon["name"])}</span></div>')
    grid = "".join(cells)
    fonts = manifest["fonts"]
    for theme, variant in (("light", "light"), ("dark", "dark")):
        for label, fam in (("Mono", fonts["mono"]["family"]), ("Colorful", fonts[variant]["family"])):
            sections.append(f'<section class="{theme}" style="--f:\'{fam}\'"><h2>{theme.title()} · {label}</h2><div class="grid">{grid}</div></section>')
    out.write_text(
        f"""<!doctype html><html><head><meta charset="utf-8"><title>Wino icons</title><style>
{''.join(faces)}
body{{margin:0;font:12px 'Segoe UI',sans-serif}}section{{padding:16px 20px}}
.light{{background:#f9f9f9;color:#1a1a1a}}.dark{{background:#202020;color:#fff}}
h2{{font-size:14px;margin:0 0 10px}}.grid{{display:grid;grid-template-columns:repeat(auto-fill,minmax(110px,1fr));gap:4px}}
.cell{{display:flex;flex-direction:column;align-items:center;padding:8px 4px;border-radius:4px}}
.cell.accent{{outline:1px dashed #8884}}.g{{font-family:var(--f);font-size:28px;line-height:1.2}}.n{{opacity:.7;font-size:10px;text-align:center;word-break:break-all}}
</style></head><body>{''.join(sections)}</body></html>
""",
        encoding="utf-8",
    )
    print(f"wrote {out}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="fail if the committed fonts differ from a fresh build")
    parser.add_argument("--verify-against", type=Path, help="compare mono outlines with another font")
    parser.add_argument("--preview", type=Path, help="write an HTML glyph sheet")
    args = parser.parse_args()

    manifest = load_manifest()
    fonts = build_all(manifest)

    if args.verify_against:
        return 1 if verify(fonts[manifest["fonts"]["mono"]["file"]], args.verify_against, manifest) else 0

    if args.check:
        stale = [str(path.relative_to(REPO_ROOT)) for path, name in output_paths(manifest) if not path.is_file() or path.read_bytes() != fonts[name]]
        if stale:
            print("stale icon fonts (run icons/tools/build_fonts.py): " + ", ".join(stale))
            return 1
        print("icon fonts are up to date")
        return 0

    for path, name in output_paths(manifest):
        path.write_bytes(fonts[name])
        print(f"wrote {path.relative_to(REPO_ROOT)} ({len(fonts[name])} bytes)")
    if args.preview:
        write_preview(manifest, fonts, args.preview)
    return 0


if __name__ == "__main__":
    sys.exit(main())
