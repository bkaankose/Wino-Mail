# Wino icons

Every icon in the app comes from the WinoIcons fonts. This folder is their source of truth.

## Add an icon

### One-time setup

Install Python 3, then create the tool environment from the repository root:

```powershell
python -m venv icons/.venv
icons\.venv\Scripts\pip install -r icons/tools/requirements.txt
```

Run the commands below with `icons\.venv\Scripts\python`, or activate the environment first.

### Steps

1. Add the icon with one of the two scripts.

   **You have an SVG file** (your own artwork, a brand logo, or an SVG you downloaded). Nothing is downloaded:

   ```powershell
   python icons/tools/add_svg_icon.py MyIcon --svg path\to\my-icon.svg
   ```

   The file can have any size or `viewBox`. The script scales it to match the other icons. Add `--color #RRGGBB` only for a brand logo that must keep a fixed color. You can also pass XAML geometry or SVG path data with `--path "<data>"` instead of `--svg`.

   For a multi-color brand logo, pass one SVG per color with `--layer`, bottom layer first. Each layer keeps its color in all three fonts, so the logo is always in color. Add `--full-bleed` to fill the whole em like the other brand logos:

   ```powershell
   python icons/tools/add_svg_icon.py Microsoft --full-bleed --layer red.svg=#F25022 --layer green.svg=#7FBA00 --layer blue.svg=#00A4EF --layer yellow.svg=#FFB900
   ```

   **You want an icon from [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons).** The script downloads that one SVG for you:

   ```powershell
   python icons/tools/add_fluent_icon.py mail_inbox MyIcon --accent blue
   ```

   `mail_inbox` is the Fluent file name without prefix, size and style (`ic_fluent_mail_inbox_24_regular`). For `--accent`, see [Colorful icons](#colorful-icons).

2. Build the fonts:

   ```powershell
   python icons/tools/build_fonts.py
   ```

3. Build the app. `WinoIconGlyph.MyIcon` and `WinoIconCodes.MyIcon` are generated automatically.

4. Use the icon:

   ```xml
   <!-- App -->
   <coreControls:WinoFontIcon Icon="MyIcon" />
   <!-- Controls library template (codepoint from manifest.json) -->
   <accountIcon:WinoFontIcon Glyph="&#xEEA6;" />
   ```

   In controls-library C#, use `WinoIconCodes.MyIcon`.

5. Map the icon in every set: add it to `icons/sets.json` (see [Icon sets](#icon-sets)), then run `import_icon_sets.py` and `build_fonts.py` again.

6. Commit:

   - the new SVG files in `icons/svg`
   - `icons/manifest.json`, `icons/sets.json` and `icons/sets/`
   - every regenerated font in `src/Wino.Mail.WinUI/Assets` and `controls/Wino.Mail.Controls.WinUI/Assets/WinoIcons.ttf`

### Do not

- Copy an SVG into `icons/svg` by hand. The scripts scale it to the font grid and register it in `manifest.json`. A file copied there is ignored.
- Edit `manifest.json` or the fonts by hand.
- Edit C# to register an icon. The source generator does this.

To replace an icon, run the same script again with the same name. The icon keeps its codepoint.

### Colorful icons

`--accent <palette key>` adds the Fluent *Filled* variant as a tinted layer for the colorful icon style.

- Use it for things and places: app modes, navigation and settings entries, folders, calendar views, people, and state such as flags, favorites, reminders and RSVP.
- Leave actions monochrome: reply, delete, sync, sort, edit, and other toolbar and menu commands.
- Keep one color per area: mail is blue, calendar is teal, people are purple, and To Do is green.

Palette keys are listed in `manifest.json` under `palettes`.

### Preview

```powershell
python icons/tools/build_fonts.py --preview icons/.preview/preview.html
```

Open the preview to see every glyph in all three fonts on light and dark backgrounds.

### Controls library templates

Inside a `ControlTemplate` in a style dictionary, the implicit `WinoFontIcon` style is not applied. Set `FontFamily="{ThemeResource WinoIconFontFamily}"` on the icon, or it falls back to Segoe Fluent Icons and shows whatever Segoe has at that codepoint. `audit-xaml-icons.ps1` checks this. Data templates are not affected.

## Icon sets

Users pick an icon set in Settings > Personalization (`IPreferencesService.IconSet`, `WinoIconSet`). Each set draws every Wino icon in one platform design language and has its own three fonts. Every set keeps the manifest's codepoints, so switching sets only switches the font.

| Set | Source | Default on |
| --- | --- | --- |
| Classic | `manifest.json` and `svg/` | — |
| Fluent | Fluent UI System Icons, 20px Regular, Filled accents (MIT) | Windows |
| MaterialOutlined | Material Symbols Outlined, weight 400 (Apache 2.0) | Android |
| MaterialRounded | Material Symbols Rounded, weight 400 (Apache 2.0) | — |
| Framework7 | Framework7 Icons, SF Symbols style (MIT) | macOS, iOS |

`sets.json` maps every icon except the brand marks to a glyph name in each library and pins each package by version and integrity hash. Brand marks keep their `manifest.json` artwork in every set. The defaults come from `IPlatformCapabilities.DefaultIconSet`.

To change a mapping, edit `sets.json`, then import and build:

```powershell
python icons/tools/import_icon_sets.py   # writes icons/sets/<Set>.json from the pinned packages
python icons/tools/build_fonts.py
```

A new icon added with `add_svg_icon.py` or `add_fluent_icon.py` also needs an entry in `sets.json`, or it keeps its Classic outline in every set. In the colorful fonts, a set's accent layer is that library's filled glyph; icons without a distinct filled glyph stay monochrome.

## Rules

- Never change a codepoint once it has shipped. Add a new glyph instead.
- Name icons after what they show (`Alert`) or what they mean in Wino (`SpecialFolderJunk`). Do not name them after Segoe codepoints.
- Palette colors live in `manifest.json` under `palettes.light` and `palettes.dark`. Dark colors are lighter and more opaque so the tint does not turn muddy on dark backgrounds.

## Checks

```powershell
python icons/tools/import_icon_sets.py --check   # committed set files match the pinned packages
python icons/tools/build_fonts.py --check   # committed fonts match the sources
.\scripts\maintenance\windows\audit-xaml-icons.ps1             # no SymbolIcon, PathIcon, Segoe glyphs or Symbol shorthand
```

## How it works

```
icons/
  manifest.json   icon name -> codepoint, aliases, accent layer, palettes, font files
  svg/            one SVG per glyph (and <Name>.accent.svg for colorful mode)
  sets.json       icon name -> glyph name per library, pinned packages (icon sets)
  sets/           imported path data per set (generated by import_icon_sets.py)
  tools/          scripts that edit the manifest and sets and build the fonts
```

The build writes three fonts per icon set into `src/Wino.Mail.WinUI/Assets`; the Mac, iOS and Android projects bundle the same files (`WinoIcons*.ttf`). It also writes the Classic monochrome font into `controls/Wino.Mail.Controls.WinUI/Assets`, because the reusable controls library packages its own copy for the playground. In the app, `NewThemeService` points the library's icons at the current set (`WinoFontIconFamily`).

| Font (Classic / other sets) | Used when |
| --- | --- |
| `WinoIcons.ttf` / `WinoIcons<Set>.ttf` | Monochrome icon style, High Contrast, and every `WinoFontIconSource` |
| `WinoIconsColor-Light.ttf` / `WinoIcons<Set>Color-Light.ttf` | Colorful icon style, light theme |
| `WinoIconsColor-Dark.ttf` / `WinoIcons<Set>Color-Dark.ttf` | Colorful icon style, dark theme |

`WinoIconFonts` in `Wino.Core.Domain` holds the file and family names; keep it in step with `set_font_specs` in `tools/wino_icons.py`.

The color fonts use COLR v0. Each glyph with an accent has two layers:

- **Accent layer:** the Fluent *Filled* shape, tinted with a palette color that includes alpha. It sits below the outline.
- **Base outline:** palette index `0xFFFF`, so it keeps following `Foreground`. Hover, pressed, selected and disabled states work as they do in the monochrome font.

Glyphs without an accent look the same in all three fonts. Brand logos with a fixed `color` or fixed-color `layers` (Google, Microsoft) are COLR glyphs in all three fonts, including the monochrome one.

`src/Wino.SourceGenerators/Icons/WinoIconGenerator.cs` generates code from `manifest.json`:

- **`WinoIconGlyph` and `WinoIconGlyphs.GetGlyph`** go into `Wino.Core.Domain`. App XAML uses `Icon="Name"`.
- **`WinoIconCodes`** goes into `Wino.Mail.Controls`, as string constants per icon. The controls library cannot reference Domain; it opts in with the `WinoIconCodesNamespace` MSBuild property.

Every SVG in `icons/svg` is in em space: `viewBox="0 0 1024 1024"`, y pointing down. The add scripts convert to this space. New glyphs get the next free codepoint. If a name is currently an alias of another icon, the alias is split into its own glyph.

`add_fluent_icon.py` downloads from the `@fluentui/svg-icons` version pinned in `manifest.json` (`fluent.version`) and caches the files in `icons/.cache`. Use `--source <dir>` to read a local copy of the package instead. Fluent's 24px grid maps 1:1 onto the em square, so these icons match the size of the existing ones.

## History

The first 111 glyphs were extracted from the original IcoMoon-built font. Their editable sources now live in `svg/` and `manifest.json`. `build_fonts.py --verify-against <old.ttf>` compares outlines with any earlier font.
