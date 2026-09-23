# Wino icons

Every icon in the app comes from the WinoIcons fonts. This folder is their source of truth.

```
icons/
  manifest.json   icon name -> codepoint, aliases, accent layer, palettes, font files
  svg/            one SVG per glyph (and <Name>.accent.svg for colorful mode)
  tools/          scripts that edit the manifest and build the fonts
```

The build writes three fonts into `src/Wino.Mail.WinUI/Assets`. It also writes the monochrome font into `controls/Wino.Mail.Controls/Assets`, because the reusable controls library packages its own copy. Commit them, but never edit them by hand.

| Font | Used when |
| --- | --- |
| `WinoIcons.ttf` | Monochrome icon style, High Contrast, and every `WinoFontIconSource` |
| `WinoIconsColor-Light.ttf` | Colorful icon style, light theme |
| `WinoIconsColor-Dark.ttf` | Colorful icon style, dark theme |

The color fonts use COLR v0. Each glyph with an accent has two layers:

- **Accent layer:** the Fluent *Filled* shape, tinted with a palette color that includes alpha. It sits below the outline.
- **Base outline:** palette index `0xFFFF`, so it keeps following `Foreground`. Hover, pressed, selected and disabled states work as they do in the monochrome font.

Glyphs without an accent look the same in all three fonts.

`src/Wino.SourceGenerators/Icons/WinoIconGenerator.cs` generates code from `manifest.json`:

- **`WinoIconGlyph` and `WinoIconGlyphs.GetGlyph`** go into `Wino.Core.Domain`. App XAML uses `Icon="Name"`.
- **`WinoIconCodes`** goes into `Wino.Mail.Controls`, as string constants per icon. The controls library cannot reference Domain; it opts in with the `WinoIconCodesNamespace` MSBuild property.

A new icon becomes available in both after the next build.

## Setup

```powershell
python -m venv icons/.venv
icons\.venv\Scripts\pip install -r icons/tools/requirements.txt
```

## Add an icon

1. Find the icon in [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons). The name is the file stem without size or style, for example `mail_inbox` for `ic_fluent_mail_inbox_24_regular`.
2. Import it:

   ```powershell
   python icons/tools/add_fluent_icon.py mail_inbox SpecialFolderInbox --accent blue
   ```

   - `--accent <palette key>` adds the Filled variant as a tinted layer for colorful mode. Use it only where color carries meaning, such as folders, status, and RSVP. Leave toolbar and menu commands monochrome.
   - The icon gets the next free codepoint. When you replace an existing icon, its codepoint is kept.
   - If the name is currently an alias of another icon, the alias is split into its own glyph.
3. For artwork that Fluent does not ship, such as brand logos, use `add_svg_icon.py <Name> --svg file.svg` or `--path "<XAML geometry>"`. Add `--color #RRGGBB` for a fixed brand color.
4. Build the fonts:

   ```powershell
   python icons/tools/build_fonts.py --preview icons/.preview/preview.html
   ```

   Open the preview to see every glyph in all three fonts on light and dark backgrounds.
5. Use the icon:

   ```xml
   <!-- App -->
   <coreControls:WinoFontIcon Icon="SpecialFolderInbox" />
   <!-- Controls library template (codepoint from manifest.json) -->
   <accountIcon:WinoFontIcon Glyph="&#xEEA6;" />
   ```

   In controls-library C#, use `WinoIconCodes.Delete`.

The SVGs are downloaded from the `@fluentui/svg-icons` version pinned in `manifest.json` (`fluent.version`) and cached in `icons/.cache`. Fluent's 24px grid maps 1:1 onto the em square, so new icons match the size of the existing ones.

## Rules

- Never change a codepoint once it has shipped. Add a new glyph instead.
- Name icons after what they show (`Alert`) or what they mean in Wino (`SpecialFolderJunk`). Do not name them after Segoe codepoints.
- Palette colors live in `manifest.json` under `palettes.light` and `palettes.dark`. Dark colors are lighter and more opaque so the tint does not turn muddy on dark backgrounds.

## Checks

```powershell
python icons/tools/build_fonts.py --check   # committed fonts match the sources
.\scripts\audit-xaml-icons.ps1              # no SymbolIcon, PathIcon, Segoe glyphs or Symbol shorthand
```

## History

`tools/extract_font.py` split the original icomoon-built `WinoIcons.ttf` into these SVGs. It is kept to show where the first 111 glyphs came from. `build_fonts.py --verify-against <old.ttf>` compares outlines with any earlier font.
