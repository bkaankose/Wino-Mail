"""One-time migration: split the icomoon-built WinoIcons.ttf into icons/svg + icons/manifest.json.

Kept in the repo to document where the original glyphs came from. Running it again overwrites
the manifest, so only do that against the original font.

    python icons/tools/extract_font.py <WinoIcons.ttf> <WinoFontIcon.cs> <ControlConstants.cs>
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

from fontTools.ttLib import TTFont

from wino_icons import SVG_DIR, draw_to_svg_path, format_codepoint, save_manifest, write_em_svg

# Glyphs that exist in the font but had no WinoIconGlyph member. Codepoints not listed here
# (the empty E900-E903 placeholders and duplicates of existing icons) are dropped.
EXTRA_NAMES = {
    0xE5ED: "AlertCircle",
    0xE71C: "Filter",
    0xE7D0: "MailBlocked",
    0xE8FA: "PersonAdd",
    0xE909: "WeatherRainSnow",
    0xE90B: "WeatherSnowflake",
    0xE90D: "WeatherSnowShowerNight",
    0xE90F: "WeatherSunnyHigh",
    0xE910: "WeatherSunnyLow",
    0xE919: "CalendarAgenda",
    0xE91F: "WeatherCloudyNight",
    0xE921: "WeatherPartlyCloudy",
    0xE927: "PersonCall",
    0xEA90: "DocumentPdf",
    0xEBDA: "PeopleCommunity",
    0xF2B7: "Translate",
    0xF3DC: "Edit",
    0xF598: "Add",
}

# CalendarWorkWeek pointed at the CalendarWeek glyph although the font has a dedicated one.
CODEPOINT_OVERRIDES = {"CalendarWorkWeek": 0xE91E}


def read_enum_order(enum_source: str) -> list[str]:
    body = re.search(r"enum\s+WinoIconGlyph\s*\{(.*?)\}", enum_source, re.S).group(1)
    return [m.strip() for m in body.replace("\n", " ").split(",") if m.strip()]


def read_glyph_map(constants_source: str) -> dict[str, int]:
    pairs = re.findall(r'WinoIconGlyph\.(\w+),\s*"\\[uU]([0-9A-Fa-f]+)"', constants_source)
    return {name: int(cp, 16) for name, cp in pairs}


def main(font_path: str, enum_path: str, constants_path: str) -> None:
    font = TTFont(font_path)
    upm = font["head"].unitsPerEm
    ascent = font["hhea"].ascent
    descent = -font["hhea"].descent
    cmap = font.getBestCmap()
    glyph_set = font.getGlyphSet()
    hmtx = font["hmtx"]
    colr = font["COLR"].ColorLayers if "COLR" in font else {}
    palette = font["CPAL"].palettes[0] if "CPAL" in font else []

    enum_order = read_enum_order(Path(enum_path).read_text(encoding="utf-8"))
    glyph_map = read_glyph_map(Path(constants_path).read_text(encoding="utf-8"))
    glyph_map.update(CODEPOINT_OVERRIDES)

    # Primary name per codepoint = first enum member using it; later members become aliases.
    by_codepoint: dict[int, list[str]] = {}
    for name in enum_order:
        cp = glyph_map.get(name)
        if name == "None" or cp is None:
            continue
        if cp not in cmap:
            print(f"skip {name}: U+{cp:X} is not in the font")
            continue
        by_codepoint.setdefault(cp, []).append(name)
    for cp, name in EXTRA_NAMES.items():
        by_codepoint.setdefault(cp, [name])

    SVG_DIR.mkdir(parents=True, exist_ok=True)
    for old in SVG_DIR.glob("*.svg"):
        old.unlink()

    icons = []
    for cp, names in by_codepoint.items():
        name = names[0]
        glyph_name = cmap[cp]
        entry = {"name": name, "codepoint": format_codepoint(cp)}
        if len(names) > 1:
            entry["aliases"] = names[1:]

        # icomoon stored the Yahoo logo as a one-layer color glyph; keep its outline and color.
        outline = glyph_name
        if glyph_name in colr:
            layer = colr[glyph_name][0]
            outline = layer.name
            c = palette[layer.colorID]
            entry["color"] = f"#{c.red:02X}{c.green:02X}{c.blue:02X}"

        d = draw_to_svg_path(glyph_set[outline].draw, ascent)
        svg_name = f"{name}.svg"
        write_em_svg(SVG_DIR / svg_name, d, upm)
        entry["svg"] = svg_name

        advance = hmtx[glyph_name][0]
        if advance != upm:
            entry["advance"] = advance
        icons.append(entry)

    manifest = {
        "$comment": "Source of truth for WinoIcons. Edit with icons/tools, then run build_fonts.py. See icons/README.md.",
        "unitsPerEm": upm,
        "ascent": ascent,
        "descent": descent,
        "fluent": {"package": "@fluentui/svg-icons", "version": "1.1.341", "size": 24},
        "fonts": {
            "mono": {"file": "WinoIcons.ttf", "family": "WinoIcons"},
            "light": {"file": "WinoIconsColor-Light.ttf", "family": "WinoIconsColorLight", "palette": "light"},
            "dark": {"file": "WinoIconsColor-Dark.ttf", "family": "WinoIconsColorDark", "palette": "dark"},
        },
        "palettes": {"light": {}, "dark": {}},
        "icons": icons,
    }
    save_manifest(manifest)
    print(f"extracted {len(icons)} icons ({sum(len(i.get('aliases', [])) for i in icons)} aliases)")


if __name__ == "__main__":
    main(*sys.argv[1:4])
