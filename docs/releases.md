# Release packages

Each platform builds its release artifacts with its own scripts on its own host. Publication to the stores or the download website is a separate maintainer task.

| Platform | Artifacts | Host | Guide | Scripts |
| --- | --- | --- | --- | --- |
| Windows | Store upload, Beta and stable sideload bundles, App Installer feeds | Windows | [Windows release packages](releases-windows.md) | `scripts/release/windows` |
| macOS | Notarized DMG with Sparkle feed, Mac App Store package | Mac | [macOS release packages](releases-macos.md) | `scripts/release/macos` |

## Before any release

The [release skill](../.claude/skills/release/SKILL.md) runs these steps; `scripts/release/release.ps1` does the mechanical work.

1. Set the new version: `pwsh scripts/release/release.ps1 bump 2.2.6`. Both platforms read it from `src/Wino.Mail.WinUI/Package.appxmanifest`: `2.2.6.0` is the Windows package version and becomes Mac app version `2.2.6`.
2. Draft the release notes: `pwsh scripts/release/release.ps1 notes`. Claude Code (`claude -p`, run locally) writes `release-notes/<major.minor.build>.md` from every commit since the previous `v*` tag, the issues labeled `pending release`, `Refs: #N` commit trailers, and `release-notes/unreleased.md`. The [format](../release-notes/README.md) separates Highlights from the full lists.
3. Review that file. It is the only text you edit. Keep only major features in Highlights, and resolve the Review section.
4. Add the What's New notes for the Highlights with the [whatsnew skill](../.claude/skills/whatsnew/SKILL.md).
   The skill writes `src/Wino.Mail.WinUI/Assets/WhatsNew/<major.minor.build>.json` and its PNG illustrations. The Mac app bundles the same files. A release without Highlights has no What's New notes.
5. Run `pwsh -NoProfile -File ./scripts/release/whats-new/validate.ps1`.
6. Render the Store text: `pwsh scripts/release/release.ps1 publish 2.2.6 -DryRun`. Paste `release-notes/.out/2.2.6/store.txt` into Partner Center's "What's new in this version" when you submit.

## After the Store build is live

Run `pwsh scripts/release/release.ps1 publish 2.2.6` (add `-Ref <commit>` when the build was not made from `HEAD`). It:

1. Tags the build commit `v2.2.6.0` and pushes the tag. The next release's notes start from this tag.
2. Creates the GitHub release, marked Latest, with the Store badge, the App Installer link, Highlights with their What's New illustrations, and the full lists.
3. For every issue linked in the notes: removes `pending release`, posts a "now available" comment with the release link, and closes the issue.

While the Store build is still in review, `publish 2.2.6 -Draft` pushes the tag and creates the release as a draft without touching the issues. The later `publish 2.2.6` turns that draft into the Latest release and then updates the issues.

GitHub Releases list stable Windows versions that are live in Microsoft Store, and nothing else. Beta builds reach testers through the beta App Installer feed only.
Between releases, mark addressed issues with `release.ps1 mark <N>` or the `pending release` label on GitHub, and add work from other repositories with `release.ps1 add "<text>"`. The commit rules in [AGENTS.md](../AGENTS.md#commits-and-release-notes) keep the next draft complete.

Every release script first asks whether to translate missing resources with `scripts/localization/translate_resources.py --apply`. A yes answer requires `WINO_OPENAI_API_KEY` and stops the release if translation fails. Each platform guide describes the switches for unattended runs.

## Shared resources

- `scripts/release/release.ps1` sets the version, drafts the release notes, and publishes GitHub releases (PowerShell 7, `gh`, and `claude`).
- `scripts/release/whats-new/validate.ps1` validates What's New notes for both platforms (PowerShell 7).
- `scripts/localization` holds the translation tools (Python 3.10 or later).
- `scripts/release/Assets/Beta` holds the Windows beta artwork.
- Fonts, theme wallpapers, and What's New assets live in `src/Wino.Mail.WinUI` and are linked into the Mac bundle by `src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj`.

Credentials for each platform are listed in the [local script environment](local-script-environment.md). Dependency publication follows the policy in [AGENTS.md](../AGENTS.md#dependencies) on every platform.
