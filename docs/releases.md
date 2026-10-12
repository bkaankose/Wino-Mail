# Release packages

Each platform builds its release artifacts with its own scripts on its own host. Publication to the stores or the download website is a separate maintainer task.

| Platform | Artifacts | Host | Guide | Scripts |
| --- | --- | --- | --- | --- |
| Windows | Store upload, Beta and stable sideload bundles, App Installer feeds | Windows | [Windows release packages](releases-windows.md) | `scripts/release/windows` |
| macOS | Notarized DMG with Sparkle feed, Mac App Store package | Mac | [macOS release packages](releases-macos.md) | `scripts/release/macos` |

## Before any release

1. Set the new version in `src/Wino.Mail.WinUI/Package.appxmanifest`. Both platforms read it: `2.0.55.0` is the Windows package version and becomes Mac app version `2.0.55`.
2. Add the What's New notes for that version with the [whats-new skill](../.claude/skills/whats-new/SKILL.md).
   The skill writes `src/Wino.Mail.WinUI/Assets/WhatsNew/<major.minor.build>.json` and its PNG illustrations. The Mac app bundles the same files.
3. Run `pwsh -NoProfile -File ./scripts/release/whats-new/validate.ps1`.

Every release script first asks whether to translate missing resources with `scripts/localization/translate_resources.py --apply`. A yes answer requires `WINO_OPENAI_API_KEY` and stops the release if translation fails. Each platform guide describes the switches for unattended runs.

## Shared resources

- `scripts/release/whats-new/validate.ps1` validates release notes for both platforms (PowerShell 7).
- `scripts/localization` holds the translation tools (Python 3.10 or later).
- `scripts/release/Assets/Beta` holds the Windows beta artwork.
- Fonts, theme wallpapers, and What's New assets live in `src/Wino.Mail.WinUI` and are linked into the Mac bundle by `src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj`.

Credentials for each platform are listed in the [local script environment](local-script-environment.md). Dependency publication follows the policy in [AGENTS.md](../AGENTS.md#dependencies) on every platform.
