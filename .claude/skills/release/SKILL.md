---
name: release
description: Prepare or publish a stable Windows release of Wino Mail. Use when the user asks to prepare a release, bump the version, draft or regenerate release notes or a changelog, mark issues for the next release, or publish a release to GitHub after the Store build is live. Runs scripts/release/release.ps1 (bump, notes, publish) and hands the What's New part to the whatsnew skill.
---

# Stable Windows release

`scripts/release/release.ps1` does the mechanical work. Read `release-notes/README.md` for the notes format and `docs/releases.md` for the full flow.
Run it with PowerShell 7 (`pwsh`). On a Mac without `pwsh` on the PATH, use `~/.dotnet/tools/pwsh`.

GitHub Releases list stable Windows versions only, one per version that is live in the Microsoft Store. Beta builds never get a GitHub release.

## Prepare (before the build)

1. **Version.** If the user gives a version and the manifest is not on it yet, run `release.ps1 bump <x.y.z>`. Commit only when the user asks.
2. **Notes.** Run `release.ps1 notes` (add `-Force` only when the user asks to redraft; it overwrites their edits). It takes a few minutes.
3. **Review.** Show the user the draft: the Highlights, the counts of New and improved and Fixes entries, and every Review item. Stop and let the user edit the file or tell you what to change. Do not continue until the user approves the notes.
   - Highlights are the only items that reach the in-app What's New. The user decides what stays there.
   - Each Review item is a question: add the issue to an entry, or drop it. Empty the Review section when everything is resolved.
4. **What's New.** Run the `whatsnew` skill for the Highlights. When Highlights is `_None_`, skip it.
5. **Validate.** Run `pwsh -NoProfile -File scripts/release/whats-new/validate.ps1`.
6. **Store text.** Run `release.ps1 publish <x.y.z> -DryRun`. Give the user `release-notes/.out/<x.y.z>/store.txt` for Partner Center's "What's new in this version", and the issue list that publish will close.

The user then builds with `scripts/release/windows/build-releases.ps1` and submits to the Store.

## Draft (Store build submitted, not live yet)

When the user asks for the GitHub release before the Store build is live, run `release.ps1 publish <x.y.z> -Draft -Yes`. It pushes the tag and creates a draft release; issues stay unchanged. The publish steps below later turn the draft into the Latest release.

## Publish (after the Store build is live)

Only when the user says the Store build is live:

1. Run `release.ps1 publish <x.y.z> -DryRun` again and show what will happen: the tag and its commit, the issues that will be commented on and closed.
2. After the user confirms, run `release.ps1 publish <x.y.z> -Yes`. Use `-Ref <commit>` when the build was made from a commit other than `HEAD`.
3. Report the release link and give the user `social.txt`.

Publishing pushes the tag, creates the GitHub release marked Latest, and for every issue linked in the notes removes `pending release`, posts a comment, and closes it.

## Between releases

- When the user says an issue is addressed, run `release.ps1 mark <N>`. When they say it is not, `release.ps1 unmark <N>`.
- When the user wants work from another repository in the notes, run `release.ps1 add "<user-facing text>"`.
- The commit rules in `AGENTS.md` (Refs: trailers and the label) keep the next draft complete.
