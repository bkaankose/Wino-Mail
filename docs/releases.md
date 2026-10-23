# Release packages

The Windows release script builds the release artifacts on a Windows host. Publication to the stores or the download website is a separate maintainer task.

| Platform | Artifacts | Host | Guide | Scripts |
| --- | --- | --- | --- | --- |
| Windows | Store upload, Beta and stable sideload bundles, App Installer feeds | Windows | [Windows release packages](releases-windows.md) | `scripts/release/windows` |

## Before any release

1. Set the new version in `src/MailCal.Mail.WinUI/Package.appxmanifest`. For example, `2.0.55.0` is the package version.
2. Add the What's New notes for that version with the [whats-new skill](../.claude/skills/whats-new/SKILL.md).
   The skill writes `src/MailCal.Mail.WinUI/Assets/WhatsNew/<major.minor.build>.json` and its PNG illustrations.
3. Run `pwsh -NoProfile -File ./scripts/release/whats-new/validate.ps1`.

Every release script first asks whether to translate missing resources with `scripts/localization/translate_resources.py --apply`. A yes answer requires `MAILCAL_OPENAI_API_KEY` and stops the release if translation fails. The [Windows release guide](releases-windows.md) describes the switches for unattended runs.

## Shared resources

- `scripts/release/whats-new/validate.ps1` validates release notes (PowerShell 7).
- `scripts/localization` holds the translation tools (Python 3.10 or later).
- `scripts/release/Assets/Beta` holds the Windows beta artwork.
- Fonts, theme wallpapers, and What's New assets live in `src/MailCal.Mail.WinUI`.

Credentials are listed in the [local script environment](local-script-environment.md). Dependencies follow the policy in [AGENTS.md](../AGENTS.md#dependencies).
