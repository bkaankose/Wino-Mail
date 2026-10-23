# 20261012-platform-workflows-2b8ce1: Separate Windows and macOS development workflows
Protocol: impl-work/v1
Owner: Claude Code desktop (Opus 5.5) session on Windows; active
Updated: 2026-10-12T09:40:00+02:00

## Purpose
Separate Windows and macOS development guidance, scripts, workspaces and build policy while keeping one repository and shared backend. Add a release-script translation step, move release artwork, delete stale folders, and write the macOS shared-logic roadmap. Documentation and tooling only; no application API changes, CI, data migration or publication.

## Agreement
Plan: Approved
Approval: 2026-10-12, user message "Start implementing the plan" after revising `C:\Users\bkaan\Desktop\Scripts Consolidation.txt`
- Plan: the five sections of `C:\Users\bkaan\Desktop\Scripts Consolidation.txt` (documentation routing, script/workspace separation, build-policy isolation, roadmap, verification), in that order.
- Decisions/constraints:
  - Translation: release scripts ask first; yes always runs `translate_resources.py --apply` (no dry run, no Python path check); stop when `MAILCAL_OPENAI_API_KEY` is missing or translation fails; `-Translate`/`-SkipTranslation` (`--translate`/`--skip-translation`), non-interactive default skips. No clean-tree check.
  - `release-assets` moves to `scripts/release/Assets`; `scripts/release-profiles` deleted; `scripts/imap-lab` already removed (Recycle Bin).
  - Routine choices (made here): Windows/macOS build imports live in `build/`; No forwarding entry points: the user decided on 2026-10-12 ("We don't need a script for forwarding the paths. As long as the documents follow new paths it's fine."); documents use the new paths only. Track `docs/macos-design-decisions.md` through an ignore exception because the new routing makes it authoritative.
  - No commit or push. Never launch the packaged executable; Release gate without deployment.

## Context
- Checkout: main; start commit: 61b7ea3adeb080d23ce14515ec569e013406dd37 (clean)
- Relevant files/materials: `AGENTS.md`, `docs/harness/*.md`, `docs/releases.md`, `docs/README.md`, `controls/AGENTS.md`, `scripts/**`, `.vscode/*`, `MailCal.MacOS.code-workspace`, `MailCal.MacOS.slnx`, `Directory.Build.props/targets`, test and shared csproj files, `.gitignore`, `.gitattributes`, `release-assets/**`
- Existing changes to preserve: none at start (local untracked docs under `docs/` are ignored and preserved). Concurrent edits appeared during T1 in `controls/MailCal.Editor.Core/Editor/editor.js` and `reader.js` (DOMPurify `ADD_FORBID_CONTENTS`); not part of this work, do not touch.
- Verification: link/JSON/syntax checks; forwarder argument/exit tests; MSBuild property evaluation (Windows, MacOS, tests, osx-arm64/x64); Debug x64 build; Release x64 gate; portable tests. macOS native builds and F5 checks are handoff unless run on `burak-mac` with user consent.

## Items
| ID | Work / dependencies | Status | Done when | Result / evidence |
| --- | --- | --- | --- | --- |
| T1 | Documentation routing (AGENTS, development selector + Windows/macOS guides, rules, controls scope, release split, indexes, historical labels, ignore exceptions); needs: none | Finished | Docs routed, links valid | All platform docs written and routed; link check over 22 changed docs: all new/changed links resolve; 4 pre-existing broken links (CONTRIBUTING PreferencesService path, 3 `thread://` links in appkit plan) left unchanged. |
| T2 | Script relocation, forwarders, release-assets move, release-profiles delete, host checks, LF rules; needs: none | Finished | New layout, forwarders tested | git mv into platform folders; forwarders and helper removed at the user's request; root resolution, Beta path, csproj import, skill/README refs updated; `scripts/**/*.sh` LF rule; host checks (F5 helper, lab, notification icons). Evidence: all .ps1 parse, bash -n ok, scripts run from outside the repo, no remaining old-path references outside this record; icon audit identical to HEAD script. |
| T3 | Release-script translation step (Windows + both macOS scripts); needs: T2 | Finished | Prompt/switches implemented, syntax-checked | `-Translate`/`-SkipTranslation` + `Invoke-ReleaseTranslation` (Windows); `release-translation.sh` sourced by both Mac scripts with `--translate`/`--skip-translation`. Stubbed tests: both-switch error, skip, non-interactive skip, missing key stop, python failure stop, success, interactive yes/no. |
| T4 | Workspaces, VS Code tasks/launch split, Mac solution tests; needs: T2 | Finished | Valid JSON, Windows folder unchanged in behavior | Folder `.vscode` keeps Windows only (new helper path); `MailCal.MacOS.code-workspace` holds Mac tasks (arm64 default build, x64 build, debugger prep, sequential prep+build F5 task) and launch with matching TMPDIR; new `MailCal.Windows.code-workspace`; ignore exceptions for both. Four test projects added to Mac slnx (`dotnet sln list` ok). JSON parse ok. Manual F5 on both hosts pending (T7/T8). |
| T5 | Build-policy isolation (`build/` imports, host platform resolution, test RIDs, F5 cache inputs); needs: T2 | Finished | Evaluated properties match expectations | Root props: host platform resolution + `build/MailCal.Windows.props` (Release RIDs); root targets: shared gates + `build/MailCal.Windows.targets` (CsWinRT, WinUI warnings, MSIX payload) + `build/MailCal.MacOS.targets` (AppKit AOT exemption) + invalid-value error. Shared TFMs and ML.Tests RIDs gated on `== 'Windows'`. Evidence: 18-case `-getProperty` diff vs baseline shows only `MailCalTargetPlatform ''->Windows` and MLTests MacOS RIDs removed; empty platform keeps gates without win RIDs; `Linux` errors; Mac head/AppKit/ML universal evaluation keeps osx RIDs and AOT exemption. F5 cache hashes `build/`. |
| T6 | `docs/macos-shared-logic-roadmap.md` from rechecked inventory; needs: none | Finished | Roadmap with phases and status | `docs/macos-shared-logic-roadmap.md`: rechecked statuses (2 resolved, 7 partial, 24 open confirmed, rest marked not rechecked) and four phases with ownership, adapters, prerequisites, tests, native checks. |
| T7 | Windows verification (builds, Release gate, tests, script checks); needs: T1-T6 | Finished | All pass or failures reported | Debug x64: 0 warnings/0 errors (`artifacts/platform-workflows/debug-x64.log`). Release x64 gate without packaging: 0/0 (`release-x64.log`). Tests Debug x64: Controls 34/34, ML 10/10, Core 1042/1043, ViewModels 144/155; the 12 failures (MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce, 11 MailCalIntelligenceHeaderPresenterTests) reproduce on an unchanged HEAD worktree, so they predate this work. No XAML changed. Windows manual F5 (local + Sandbox) not run: it deploys the package. |
| T8 | macOS verification handoff; needs: T7 | Blocked | Exact pending Mac checks recorded (or run) | Mac checks run on burak-mac (192.168.1.50, macOS 26.7.1, SDK 10.0.401, workload 27.0.10722, Xcode 27) from a clean clone of 145574e in `~/mailcal-validate`, ad hoc signing: host resolves MacOS with no win RIDs and Release gates on; universal head keeps osx RIDs and AOT exemption; ML osx-x64 unsupported. Debug arm64/x64, host-resolved arm64, Release arm64/x64 and Mac solution: 0 errors; codesign verify, `com.mailcal.macos`, arch, CoreCLR and entitlements ok for all four bundles. Tests: Controls 34/34, ML 10/10, Core 1040/1043, ViewModels 144/155 — identical failures and warning count (54) at baseline 61b7ea3. Script syntax, --help flags, conflicting-flag error, workspace JSON ok. Remaining: manual F5 on Mac (Apple Silicon) and Windows (local + Sandbox). |

## macOS handoff (T8)
Run on an Apple Silicon Mac from this checkout (not run here; requires the Mac):
1. `dotnet build src/MailCal.Mail.MacOS/MailCal.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:MailCalTargetRuntimeIdentifier=osx-arm64 -p:MailCalTargetPlatform=MacOS -p:EnableWindowsTargeting=true`, then the same with `osx-x64`.
2. Same builds without `-p:MailCalTargetPlatform=MacOS` (host resolution) and `dotnet msbuild src/MailCal.Core/MailCal.Core.csproj -p:Configuration=Release -getProperty:MailCalTargetPlatform,RuntimeIdentifiers` (expect `MacOS`, no `win-*`).
3. Release arm64 and x64 builds (`-c Release`), plus `-p:RuntimeIdentifiers=osx-arm64%3Bosx-x64` universal evaluation; `codesign --verify --deep --strict`, effective entitlements, `CFBundleIdentifier=com.mailcal.macos`.
4. `dotnet build MailCal.MacOS.slnx -c Debug -p:MailCalTargetPlatform=MacOS -p:EnableWindowsTargeting=true` and the four portable test projects (commands in `docs/harness/development-macos.md`).
5. `bash scripts/release/macos/build-macos-release.sh --help` and the App Store script `--help` (new flags listed).
6. Manual F5 in `MailCal.MacOS.code-workspace`: Ctrl+Shift+B builds without debugger prep; F5 runs prep then build, breakpoint at `Program.cs:9`, continue to shell. Do not notarize or publish.

## Resume
- Current: T8 (blocked)
- Next: manual F5 on both hosts; then delete this record
- Blockers: T8 needs manual F5 runs on both hosts
- Unfinished operations: none

## History
- 2026-10-12T08:54+02:00 | plan | Finished | Plan approved; record created after repository survey.
- 2026-10-12T09:40+02:00 | T1 | In Progress | Platform docs written; .gitignore now `/docs/*` with narrow exceptions (check-ignore verified).
- 2026-10-12T10:20+02:00 | T2,T3 | Finished | Script relocation and translation step implemented and tested (see rows).
- 2026-10-12T10:55+02:00 | T4,T5 | Finished | Workspaces and build-policy isolation implemented; property evaluation diff clean.
- 2026-10-12T11:30+02:00 | T1,T6 | Finished | Docs link check clean for new links; roadmap written. T7 started.
- 2026-10-12T12:20+02:00 | T7 | Finished | Windows builds pass; pre-existing test failures confirmed on HEAD. T8 blocked on Mac/manual checks.
- 2026-10-12T12:40+02:00 | T2 | Finished | Forwarders and `scripts/Invoke-RelocatedScript.ps1` removed per user; docs reference new paths only.
- 2026-10-12T13:30+02:00 | T8 | Blocked | Automated Mac validation passed over SSH; results match baseline; only manual F5 remains.
