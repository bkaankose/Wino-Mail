# Wino Mail for Android: implementation handoff prompt

Copy everything below the line into the first message of a new Claude Code session opened in a checkout of `bkaankose/Wino-Mail`. It carries the decisions and memory from Burak's account as of 13 October 2026.

---

You are starting the Android port of Wino Mail. The owner (Burak) ran the macOS and iOS ports with the process below and wants Android to follow the same principles exactly. Read this whole message before touching anything. It replaces the memory you do not have.

## 1. Where the repository stands

- Branch to start from: `feature/iOS` (pushed to origin). It contains the iOS/iPadOS port and, more importantly for you, the shared-layer work Android depends on: `RegisterWinoViewModels()`, `NavigationCoordinator`, `NotificationResponseService`, `ActivationRouter`, `ApplicationLifecycleCoordinator`, the `IGoogleAuthorizationCodeReceiver` seam, `IApplicationRuntime.SynchronizeOnceAsync`, and the neutral `controls/Wino.Presentation.Core` binding engine. These were the "A0" items of the Android investigation; most of them are done, so re-verify rather than redo them.
- Branch off `feature/iOS` into `feature/Android`. Do not wait for a merge to `main`.
- Read `AGENTS.md` first. Platform heads are thin; business rules live in `Wino.Core.Domain`, `Wino.Services`, `Wino.Mail.ViewModels`, `controls/Wino.Mail.Controls.Core`, `controls/Wino.Editor.Core`. Never put a rule in the Android projects that Windows, Mac or iOS would have to copy.
- The iOS work is the template for everything: project layout, build interface (`WinoTargetPlatform`, `build/Wino.iOS.targets`, `WinoMail.iOS.slnx`), docs (`docs/ios-design-decisions.md`, `docs/harness/development-ios.md`), and the work record `iq/20261013-ios-platform-a3f9c1.md` with agent reports in `iq/20261013-ios-platform-a3f9c1/`. Mirror them as `src/Wino.Mail.Android`, `src/Wino.Platform.Android`, `controls/Wino.Presentation.Android`, `controls/Wino.Mail.Controls.Android`, `controls/Wino.Editor.Android`, `build/Wino.Android.targets`, `WinoMail.Android.slnx`, `docs/android-design-decisions.md`, `docs/harness/development-android.md`, and an `AGENTS.md` row and routing entry for Android.

## 2. Documents and artifacts you must read before planning

1. `docs/android-port-investigation.md` (10 Oct 2026). Sections 3, 5, 6, 7, 8 and 11 are the inventory and package order. Section 9 lists decisions D1 to D13; several are now settled differently (section 3 below).
2. `docs/macos-design-decisions.md` (Revision 2 is the parity rule set), `docs/ios-design-decisions.md`, `docs/wino-design-guideline.md`, `docs/macos-shared-logic-roadmap.md` (remaining "Move" items; do not create a third copy of any Mac rule).
3. `docs/harness/implementation-rules.md`, and `docs/harness/development-windows.md` for the Release gate you must run on every shared change.
4. Design canvases (Claude Design artifacts, you have access):
   - Android: https://claude.ai/artifact/NDKJ6H5EJ8FR8Qi8CqnBTc. Fourteen shell boards plus Calendar, Contacts and To Do boards, phone and tablet each, built on the Wino Design System with WinoIcons fonts and the Windows wallpapers. Its "Decisions" board holds the settled decisions, the screen-to-Android mapping, the responsiveness rules and the open questions. Read the Decisions board and the assumptions sticky on the cover first.
   - macOS parity canvas (source of the tablet layouts): https://claude.ai/artifact/JJXoYw35R5XSeWGTkpJAPy.
   - iOS canvas (source of the phone flow): https://claude.ai/artifact/9iAW48NVfnnMNZTh39YhQL.

## 3. Decisions already settled by the owner. Do not re-ask them.

- **Stack:** .NET for Android, classic Android Views with **XML layouts** and the **native control set**. AndroidX and Material Components are mandatory. This overrides investigation decision D13 (code-first). No MAUI, no Uno, no Compose.
- **minSdk 23 (Android 6.0), targetSdk 36.** Overrides D4. Capability gates on the canvas: blur only on API 31+, predictive back 33+, notification channels 26+.
- **Runtime:** Mono full AOT + LLVM + trimming for Release, `PublishAot=false`, codebase stays Native AOT-clean (the Windows Release gate enforces it). Same as the Mac and iOS heads. Keep a standing `PublishAot=true` spike as a non-blocking gate.
- **Form factors:** portrait and landscape, responsive from day one, layout chosen by window size class, not orientation. Compact = phone portrait, medium = phone landscape and small tablets, expanded = tablets with the full Windows three-pane layout.
- **Modes:** Mail, Calendar, Contacts, To Do in that order. **Settings is never a mode**; the account avatar opens it (full-screen on phones). Same rule as iOS and Mac Revision 2.
- **Parity rule:** tablets reproduce the Windows layout one to one in Wino zones (pinned drawer with the mode switcher card at its foot, list pane, reader with the command bar on top, centred search, briefing as a right overlay, What's New, avatar). Phones get a simpler single-pane flow (Mailboxes → folder list → message; compose, Move and briefing as sheets; reader commands in a bottom app bar; leading swipe Archive, trailing Delete/Flag/More; long press for rare commands; Focused/Other as a two-item toggle group). Phones drop hover actions, density, reading-pane options, Companion, detached compose and global hotkeys.
- **Visual language:** theme wallpaper paints the whole screen with blurred bars, content sits in one rounded Wino zone (radius 10), WinoIcons glyphs everywhere for Wino concepts (no Material icons for folders, modes or actions that have a WinoIcons glyph), Wino row designs for mail and folder rows, accent badges, settings card and expander. Onboarding uses the Wino Mail logo (`src/Wino.Mail.WinUI/Assets/WinoLogo.png`).
- **Calendar, Contacts, To Do:** tablets use the Windows layouts in Wino zones exactly as the Mac parity canvas (WireCalendar, WireToDo, WireContacts); phones use agenda list, indexed contact list with fast scroller, and My Day with smart-view chips, as drawn on the Android canvas.
- **Background model (supersedes D6):** phone builds do not run the desktop `ApplicationRuntime` timers or in-process IMAP IDLE. Android runs "foreground sync while visible + push-triggered sync" through `SynchronizeOnceAsync`; new-mail notifications come from a Wino push relay (watcher plus doorbell: IDLE and Gmail/Graph watch on the server, FCM push carrying only account, folder and UID; the device fetches headers and does all reading and sending). The relay server itself is out of scope for the port, but the runtime mode, the FCM registration seam and the notification path must be designed for it. Do not propose WorkManager-only or periodic polling as the primary model; WorkManager is only for flushing pending changes and reminders.
- **Settings pages** keep the Windows groups, names and order (the "where was that setting" rule).
- **Process rules from the owner:** plan first and get it reviewed before code; list open decisions with a recommendation; verify provider and platform facts against documentation, never from memory; "do whatever you think is best, do not overengineer"; prefer reusing existing mechanisms over new abstractions; never commit or push unless explicitly asked in the current request (a local branch is fine, everything stays uncommitted on the machine); add English strings only to `src/Wino.Core.Domain/Translations/en_US/resources.json`; nullable diagnostics are defects; collection expressions targeting read-only interfaces need a concrete backing type.
- **Delegation:** for the implementation phases the owner wants the main session to orchestrate several Opus 5.5 subagents at medium effort with non-overlapping scopes (worktree isolation when builds collide), each writing a report under `iq/<work-id>/`, and the orchestrator integrating and running the combined verification. Use `/start-impl` to create the work record and `/continue-impl <work-id>` to resume it; the iOS record shows the expected shape.

## 4. Items still open. Confirm these with the owner before the plan is final.

Investigation recommendations not yet confirmed: D5 Google sign-in via custom scheme + Custom Tabs (owner must confirm the Cloud Console toggle and register the Android client with both SHA-1s), D7 entitlement sign-in only in v1 with Play Billing later, D8 deferrals (S/MIME, device contacts, Magika/ONNX; keep printing/PDF; exact alarms with inexact fallback), D9 data under `NoBackupFilesDir` with Auto Backup excluded, D10 package id `com.winomail.android` (note the Mac and iOS apps share `com.winomail.macos`; ask whether Android should follow that pattern), x86_64 for the emulator only or also for Play.

Canvas assumptions marked "for confirmation" on the Decisions board: compact = bottom navigation + modal drawer + FAB for new mail; medium = navigation rail + 360 dp list + reader; expanded = Windows layout with pinned drawer and mode card; FAB on phones versus rail or toolbar button; bottom navigation versus drawer footer card on compact; landscape two-pane on every phone; phone calendar defaulting to the agenda list or a day grid; Play distribution versus sideload first (drives 16 KB pages, policy forms, account deletion requirements).

Not yet on the canvas (design them in the same style before implementing, and get them reviewed): event, contact and task editors, month view, onboarding, Move and briefing sheets, notification layouts, dark variants of every screen.

## 5. What to deliver first

1. A plan (plan mode) that follows the investigation's package order A0 to A8, states which A0 items `feature/iOS` already delivered and which "Move" items from the macOS roadmap remain, lists the open items above with your recommendation, and defines the prototype gate: A1 + A2 (project skeleton compiling the portable graph for `net10.0-android`, Keystore protector and credential stores, MSAL Android host, Google custom-scheme receiver, onboarding) running on an emulator against real OAuth before any shell work.
2. After approval, the `iq/` work record and the orchestrated implementation.
3. Verification per `AGENTS.md`: Windows Debug x64 and the Release gate on every shared change, affected unit tests, Android Debug emulator build and Release Mono AOT publish, manual checks on phone and tablet emulator profiles in light and dark. Mac and iOS builds are a handoff to the owner when no Mac is available; report them as pending, never as done. A build is not proof of runtime behavior.

The owner's local IMAP lab (`tools/local-lab`, five servers, all passwords `1`) is Windows-local on 127.0.0.1; from the emulator reach it via `10.0.2.2`.
