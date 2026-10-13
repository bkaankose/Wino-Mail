# 20261013-ios-platform-a3f9c1: iOS/iPadOS platform targets over a shared Apple layer
Protocol: impl-work/v1
Owner: Claude Code desktop (Fable 5.1) session on Windows; active
Updated: 2026-10-13T14:10:00+02:00

## Purpose
Implement the approved iOS/iPadOS platform plan (`iq/20261013-ios-platform-a3f9c1/plan.md`): move Mac head business rules into shared code (Phase 0), extract `Wino.Platform.Apple` + `Wino.Core.Apple.Bindings` (Phase 1), UIKit presentation/controls (Phase 2), the iOS head with Phone and Pad UI projects (Phase 3+). Mac app stays green after every phase. No commit or push. (This record was zero-filled by a PC crash on 2026-10-13 ~02:30 and rewritten at 08:05 from the orchestrator's state; the agent reports in `iq/20261013-ios-platform-a3f9c1/*.md` are the primary evidence.)

## Agreement
Plan: Approved
Approval: 2026-10-13, user approved the plan in plan mode after answering four decisions (one universal app + Phone/Pad UI projects; `Wino.Platform.Apple` + `Wino.Core.Apple.Bindings`; iOS 17; all four modes at full fidelity). User then asked (2026-10-13) to "Do the implementation with multiple Opus 5.5 - Medium agents. Orchestrate them." and to shut down the Mac then this PC at 95% usage or when the implementation finishes.
- Plan: phases 0 → 1 → 2 → 3 → phone Mail → pad Mail → other modes → settings/store/notifications/background → docs.
- Decisions/constraints: bundle `com.winomail.ios`; Mono AOT, `PublishAot=false`; business rules in Domain/Services/ViewModels/Controls.Core, never in Apple projects; `Wino.Platform.*` never references presentation; Phone/Pad reference `Wino.Mail.UIKit` only. Out of scope: APNs relay wiring, Share Extension, widgets, Catalyst, S/MIME on iOS.

## Context
- Checkout: feature/iOS; start commit: 80ca7f38 (clean). All work is uncommitted and staged in the main index.
- Mac host `burak-mac` (192.168.1.50), checkout `~/Desktop/Wino-Mail` read-only; agent mirrors `~/Desktop/wt-*`. Mirror rule: tar from Git Bash excluding `.git .claude bin obj artifacts AppPackages .vs .codegraph .codex .prototypes node_modules`; copy the two `.xcframework` folders from a Mac-side tree (Windows stores their symlinks as text, MT7091 otherwise). iOS workload 27.0.10722 and iOS 27.0 Simulator runtime installed on the Mac.
- Delegation: Opus 5.5 medium agents, worktrees seeded with `artifacts/ios-platform/patches/<seed>.patch`; deltas computed with `artifacts/ios-platform/patches/delta.sh` and applied with `git apply --3way`.
- Verification: Windows Debug x64 + Release gate, tests; Mac Debug arm64; iOS Debug `iossimulator-arm64`; simulator launches with screenshots (`artifacts/ios-platform/*.png`). IMAP lab is Windows-local (127.0.0.1), so real-account checks in the Simulator are pending.

## Items
| ID | Work / dependencies | Status | Done when | Result / evidence |
| --- | --- | --- | --- | --- |
| T1 | Shared `RegisterWinoViewModels()`; needs: none | Finished | both roots call it | A (`A-navigation.md`) |
| T2 | `NavigationCoordinator` + `INavigationHost`; needs: T1 | Finished | Mac unchanged | A; 13 tests |
| T3 | `NotificationResponseService` + Apple UN delegate; needs: none | Finished | | B (`B-notifications-activation.md`); delegate moved to Platform.Apple by J |
| T4 | `ActivationRouter`, `ApplicationLifecycleCoordinator`; needs: T2 | Finished | | B; overlap `IApplicationShellHost` vs `INavigationHost` noted in `E-integration.md` |
| T5 | Presentation rules → Domain/ViewModels/Controls.Core; needs: none | Finished | | C (`C-presentation-rules.md`); WinUI follow-ups listed there |
| T6 | Seams: Google receiver, `SynchronizeOnceAsync`, Broker Windows-only, platform name; needs: none | Finished | | D (`D-seams-presentation-core.md`) |
| T7 | `controls/Wino.Presentation.Core`; needs: none | Finished | | D |
| T8 | Phase 0 integrated verification; needs: T1-T7 | Blocked (manual) | | E (`E-integration.md`): WinUI Debug/Release 0/0, Core 1108/1109 (known failure), ViewModels 186/186, Controls 58/58, ML 10/10, Mac Debug arm64 0 errors; manual runtime checks pending |
| T9 | `Wino.Platform.Apple`; needs: T8 | Finished | | F (`F-platform-apple.md`) |
| T10 | `Wino.Core.Apple.Bindings`, iOS slices; needs: T9 | Finished | | F: 3-slice `WinoStoreKit2.xcframework` |
| T11 | Phase 1 verification; needs: T10 | Blocked (manual) | | F builds: Mac arm64/x64/AppStore 0 errors, iOS libs 0 errors; manual launch/Keychain/notification checks pending |
| T12 | Build interface + `WinoMail.iOS.slnx`; needs: T9 | Finished | | G (`G-build-interface.md`); slnx by H |
| T13 | UIKit presentation/controls, `Wino.Editor.WebKit`; needs: T7, T12 | Finished | | H (`H-uikit-controls.md`); Controls tests 86/86 |
| T14 | `Wino.Platform.iOS`, `Wino.Mail.UIKit`, Phone/Pad shells, `Wino.Mail.iOS` head, first simulator launch; needs: T13 | Finished | launches to Welcome | I (`I-platform-ios.md`), J (`J-ios-head.md`): iPhone 18 Pro + iPad Pro 11" Welcome screenshots `J-*.png` |
| T15 | Phone Mail; needs: T14 | Finished (code; manual real-account checks pending) | boards accepted | K (`K-phone-mail.md`, delta `K-delta.patch`): Mailboxes, list, reader toolbar, Move/filter sheets; screenshots K-01..08; WinoIcons sizing bug workaround; needs M1 pages registered |
| T16 | Pad Mail; needs: T14 | Finished (code; manual checks pending) | boards accepted | L (`L-pad-mail.md`, `L-delta.patch`): shell, sidebar, list page, reader frame host, popovers, key commands; needs M1/M2 page routes, multi-scene in head |
| T15b | Shared Mail pages (reader, compose, idle, EML, briefing, What's New); needs: T14 | Finished (N: `N-mail-integration.md`, Mail flow proven on both simulators, icon sizing fixed at source, nullable navigation parameters fixed across Domain/ViewModels/Mac) | | M1 (`M1-mail-pages.md`, `M1-delta.patch`): needs `RegisterMailViews` call, `RegisterMailPages(phone:)` in registries, `OpenMailFileAsync` → `EmlReaderSheet` |
| T18a | Settings pages, account sheets, dialogs; needs: T14 | Finished (Q integrated: full-screen sheet on both idioms, routes via `SettingsPageRoutes`, 7 strings, icon workaround removed; manual checks pending) | | M2 done (report `M2-settings-dialogs.md`, delta `M2-delta.patch`, 76 files): 34 pages, 40 routes, all dialogs real; needs `RegisterSettingsViews(services);` call in `RegisterUIKitViews`, 7 `IOS_*` string keys, WinoIcons sizing fix (N). Awaiting apply after N. |
| T17 | Calendar, Contacts, To Do on both idioms; needs: T15, T16 | Finished (code; S: shell hooks replace workarounds, calendar visuals fixed; manual checks pending) | boards accepted | O2 done (report `O2-contacts-todo.md`, delta `O2-delta.patch`, 33 files): Contacts + To Do on both idioms with seeded data screenshots; iPad editors are sheets (navigation core limitation), 4 `IOS_*` strings. O1 done (report `O1-calendar.md`, delta `O1-delta.patch`): day/week/month/agenda on iPhone, day/week/work-week/month + sidebar + details column on iPad, quick event, `seedcalendar`; 3 strings; shell-hook workarounds (shell-host wrapper for composer routing, pushed sidebar, pinned column widths) to clean up after Q; apply after Q. Note: `.gitignore` ignores `Debug/` folders; N renamed K's folder to `Mail/Diagnostics`. |
| T18 | Store, notifications, background refresh wiring; needs: T17 | Blocked (device) | manual checks | S: purchase service start/sync wired like the Mac; notifications, badge, BG tasks, remote registration wired; BG tasks and push need a signed device build. |
| T19 | Docs: ios-design-decisions, development-ios, AGENTS, README, rules, bindings README; needs: T14 | Finished (R + S follow-ups; links resolve) | links valid | R done and applied (report `R-docs.md`): new `docs/ios-design-decisions.md`, `docs/harness/development-ios.md`, `WinoMail.iOS.code-workspace`, AGENTS/README/rules/roadmap/bindings README updated, `.gitignore` allow lines; 99 links resolve. Follow-ups: `docs/harness/development.md` iOS row, Calendar section after O1 merge, iPad Settings host decision. |

## Resume
- Current: implementation complete as far as the Simulator allows (S report `S-final-integration.md`: all builds 0 errors, tests green except the known failure, full walk on both idioms, 127 screenshots). 709 files staged, uncommitted; no worktrees.
- Next: user actions (App ID/profile/App Store record for `com.winomail.ios`, Azure redirect `msauth.com.winomail.ios://auth`, Google iOS OAuth client + head `IAuthenticatorConfig`), signed device build, real-account manual checks (consolidated list in the S report), What's New per-platform notes decision, `NavigationCoordinator` follow-up (10 backs past Settings walk history into another mode), later: APNs relay, Share Extension.
- Blockers: manual/device checks need a Mac user and a reachable IMAP lab; items T8/T11/T18 stay Blocked on them.
- Unfinished operations: none.

## History
- 2026-10-13T00:00+02:00 | plan | Finished | Plan approved; record created.
- 2026-10-13T02:30+02:00 | T1-T7 | In Progress | PC restarted unexpectedly; agents A-D resumed; record file zero-filled by the crash (discovered 08:00).
- 2026-10-13T03:35+02:00 | T1-T8 | Finished/Blocked | Phase 0 integrated (E), G build interface applied.
- 2026-10-13T04:50+02:00 | T13,T14 | In Progress | 5-hour usage limit reached; H and I cut off; resumed 05:50 at the user's request.
- 2026-10-13T06:00+02:00 | T9-T13 | Finished | F, H, I merged.
- 2026-10-13T06:55+02:00 | T14 | Finished | iOS app launches on iPhone and iPad simulators (J).
- 2026-10-13T08:05+02:00 | T15,T16,T15b | In Progress | K, L, M1 finished; deltas exported; record rewritten.
- 2026-10-13T08:15+02:00 | T15-T17 | In Progress | L/M1/K merged cleanly; N, O1, O2 launched.
- 2026-10-13T09:10+02:00 | all | In Progress | PC restarted after the 08:15 shutdown; Mac off until ~11:15; N, O1, O2, M2 resumed; user cancelled the shutdown cron.
- 2026-10-13T12:30+02:00 | T15-T19 | In Progress | N, M2, O2, O1, R merged; Q verified green (Windows gate, Mac, iOS Debug+Release); S launched.
- 2026-10-13T14:10+02:00 | T15-T19 | Finished/Blocked | S: Calendar shell hooks, calendar visuals, Q leftovers, T18 wiring, docs; final verification green on Windows, Mac and iOS; record and memory updated.
