---
name: whats-new
description: Write the release notes for Wino's What's New window before a release. Use when the user gives a list of features for the next version (after bumping Package.appxmanifest) and asks for What's New notes, release highlights, or illustrations. Finds each feature in git history, writes a short user-facing description, renders a 1120x600 space-gradient illustration into Assets\WhatsNew, writes Assets\WhatsNew\<version>.json, and validates it.
---

# What's New release notes

Release flow: bump `src/Wino.Mail.WinUI/Package.appxmanifest` → run this skill → `scripts/build-releases.ps1`.
The release script runs `scripts/whats-new/validate.ps1` and warns when notes for the manifest version are missing or invalid.

The app reads every `src/Wino.Mail.WinUI/Assets/WhatsNew/*.json` file (`WhatsNewService`). It lists the versions newest first in the What's New window.
The title-bar button appears only when a file exists for the running package version.

## Input

The user supplies feature names, each with a sentence about what it does.
The user also decides which versions are starred. Set `isStarred` to `true` only when the user asks for it.

## 1. Find the version and the change range

1. Read `Version` from `Package.appxmanifest`. The notes version is the first three parts (`2.1.4.0` → `2.1.4`).
2. Find the previous notes version: the highest `Assets/WhatsNew/<version>.json` below the new one.
3. Find the base commit, which is the commit that set the manifest to that previous version:
   `git log --format="%h %ad %s" --date=short -G'Version="X.Y.Z.0"' -- src/Wino.Mail.WinUI/Package.appxmanifest`
   Use the oldest match. If nothing matches (for example, for the first notes file), ask the user for the base commit or tag.
4. If `<version>.json` already exists, update it. Do not create a second file.

## 2. Identify each feature

For each feature the user lists:

1. Search `git log <base>..HEAD --format="%h %s"` and `git log <base>..HEAD -S"<keyword>" --stat` for commit messages and diffs.
2. Use CodeGraph (`codegraph explore "<feature words>"`) to find the code and the XAML that shows the feature.
3. If two or more changes match, or none do, ask the user which change they mean. Do not guess.

## 3. Write the copy

- **Title:** 2–5 words, sentence case, no final period. Name the benefit, not the component ("Colorful icons", not "WinoIconsColor font").
- **Description:** 3–4 short lines, 280 characters or fewer. Write for a non-technical user:
  - First say what they can now do, then where to find it (for example, "Settings > Personalization").
  - Use active voice and present tense. Use "you".
  - Make it sound exciting, but keep it concrete. Do not use internal names, file names, class names, "AOT", "refactor", or version jargon.
  - Do not promise things the change does not do.
- English only. The JSON strings are not translated.

## 4. Find what to draw

Find the UI that the feature changed. Use the XAML found in step 2 (`src/Wino.Mail.WinUI/Views`, `Controls`, `Styles`).
If you cannot find where the feature appears in the UI, **ask the user** where it is or what the illustration should show, and wait for the answer.

## 5. Build the illustration

The PNG is the only output. The HTML scene is a scratch file: write it in the session scratchpad and never add it to the repository.

1. Copy [illustration-template.html](illustration-template.html) (next to this file) to `<scratchpad>/<image-name>.html`.
2. Keep the template's `:root` space tokens and the `body` background unchanged. Every illustration shares this background.
3. Replace the content of `<main class="scene">` with one of these:
   - **Mimic** (preferred): a simplified recreation of the part of the UI that changed, built with the template classes (`.stage`, `.card`, `.shimmer`, `.selected`, `.pill`, `.button`). Let the stage overlap the canvas edge so part of the gradient shows first.
     - Show only what the feature is about. Replace the rest with shimmer bars.
     - Use made-up content only ("Project kickoff", "Alex", `@contoso.com`). **Never** copy real mail, names, or addresses. **Never** use screenshots of the running app.
     - For Wino icons, inline the SVG path from `icons/svg/<Name>.svg`. For the colorful style, add the `<Name>.accent.svg` layer filled with the dark palette color from `icons/manifest.json`.
   - **Motif:** when the feature has no clear visual (for example, speed or sync reliability), draw a simple SVG motif on the same background.
4. Keep text in the scene large and short. It is shown at 560x300.
5. Choose a descriptive kebab-case image name for the feature, for example `colorful-icon-style.png` or `calendar-work-week-view.png`. Do not use random text, numbers, or version numbers.
6. Take a 1120x600 PNG of the scene with headless Edge. The scene is 560x300 CSS pixels, captured at scale 2:
   ```powershell
   & "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe" --headless=new --disable-gpu --hide-scrollbars --user-data-dir="<scratchpad>\edge" --force-device-scale-factor=2 --window-size=560,300 --virtual-time-budget=2000 --screenshot="D:\Wino-Mail\src\Wino.Mail.WinUI\Assets\WhatsNew\<image-name>.png" "file:///<scratchpad>/<image-name>.html"
   ```
7. Open the PNG with the Read tool and check it. Is the text readable? Does the feature stand out? Is the gradient visible and calm? Is nothing clipped by mistake? Fix the scene and render it again if needed.

## 6. Write the notes file

`src/Wino.Mail.WinUI/Assets/WhatsNew/<version>.json`:

```json
{
  "version": "2.1.4",
  "isStarred": false,
  "features": [
    {
      "image": "colorful-icon-style.png",
      "title": "Colorful icons",
      "description": "Give Wino a splash of color. Folders, calendar views, flags and reminders get a soft tint that makes them easier to spot. Turn it on in Settings > Personalization."
    }
  ]
}
```

- Put the most important feature first. The window selects it by default.
- `image` is the file name only. The app loads it as `ms-appx:///Assets/WhatsNew/<image>`.

## 7. Packaging and validation

- The PNGs are packaged by the Windows App SDK `**/*.png` Content glob. The JSON is packaged by `<Content Include="Assets\WhatsNew\*.json" />` in `Wino.Mail.WinUI.csproj`. Do not add per-file csproj entries.
- Run `pwsh -NoProfile -File .\scripts\whats-new\validate.ps1`. It checks the JSON, the version, the image names, the 1120x600 size, and the csproj rules. Fix every failure.
- Show the user the final titles, descriptions, and each PNG (send the PNGs with SendUserFile) for approval before they run the release script.
- Do not build, deploy, or change `Package.appxmanifest`. The user runs `scripts/build-releases.ps1`.
