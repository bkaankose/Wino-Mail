# Release notes

Each stable Windows release has one notes file, `release-notes/<major.minor.build>.md`. It is the only file the maintainer edits.
`scripts/release/release.ps1 notes` drafts it, and every public text is rendered from it: the GitHub release, the Microsoft Store "What's new in this version" text, and the short social post.
The in-app What's New window uses only the **Highlights** section, through the [whatsnew skill](../.claude/skills/whatsnew/SKILL.md).

See the [release guide](../docs/releases.md) for the full flow.

## Format

```markdown
---
version: 2.2.6
base: v2.2.5.0
---

One or two sentences that introduce the release.

## Highlights

- **Ready right after you sign in.** Wino now starts within seconds of signing in to Windows.

## New and improved

- Chinese (Traditional) is now available as an app language.

## Fixes

- Subjects with Cyrillic characters are shown correctly ([#1112](https://github.com/bkaankose/Wino-Mail/issues/1112)).

## Review

- Possibly related: [#1149](https://github.com/bkaankose/Wino-Mail/issues/1149) was closed during this release but no commit references it.
```

| Section | Contents | Published |
| --- | --- | --- |
| Front matter | `version` and the `base` tag the draft was built from | No |
| Intro | One or two sentences | GitHub release |
| Highlights | 0–4 major features, each `- **Title.** Description` | GitHub, Store, in-app What's New |
| New and improved | Smaller features and polish | GitHub, Store |
| Fixes | Bug fixes | GitHub, Store |
| Review | Questions for the maintainer: possibly related issues, issues with no matching change | Never |

- **Highlights** are new user-visible capabilities or clearly changed workflows that deserve an illustration. Bug fixes, internal work, and small polish never qualify. Performance work qualifies only when users would notice it. Write `_None_` when a release has no highlights: the release then ships without in-app What's New notes.
- **Issue links** are always full links, `([#N](https://github.com/bkaankose/Wino-Mail/issues/N))`, because the Store and social texts do not auto-link `#N`. One change that several issues describe is one entry with all of its links.
- **Every issue linked** in Highlights, New and improved, or Fixes is commented on and closed by `release.ps1 publish`. Remove a link to keep its issue open.
- **Changes without an issue** are listed too. Internal work (refactoring, build, tooling, documentation, tests) and changes that only affect platforms other than Windows are left out.
- Write for users: active voice, present tense, no class names, project names, or internal jargon.

## unreleased.md

`release.ps1 add "<text>"` appends a line to `release-notes/unreleased.md` for user-visible work that has no commit in this repository, such as a server change in Wino-Mail-API. `release.ps1 notes` includes those lines in the draft and empties the file.
