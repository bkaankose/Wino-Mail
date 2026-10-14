# 20261013-relay-live-notifications-eb032f: WinoRelay live notifications client
Protocol: impl-work/v1
Owner: Claude Code desktop main session; released
Updated: 2026-10-14T13:09:04.8282384+02:00

## Purpose
Register devices and IMAP mailboxes with WinoRelay for the signed-in Wino Account and turn relay pushes into inbox syncs, on iOS (always on), macOS and Windows (opt-in). No relay or API repo changes.

## Agreement
Plan: Approved
Approval: 2026-10-13, plan approved via ExitPlanMode after "Get the latest main, read the docs and catch up with the changes to adopt this plan."
- Plan: [plan.md](20261013-relay-live-notifications-eb032f/plan.md) (relay token from POST /api/v1/relay/token, X-Device-Id, IMAP builder, push handler, platform adapters, settings UI).
- Decisions/constraints: orchestrate with Opus 5.5 medium subagents; Mac builds over SSH (`burak-mac`); no commits or pushes; English strings only; Native AOT rules.

## Context
- Checkout: feature/iOS; start commit: 73737c37. Relay main 811ea93->f0583c1, API main d72ca2b->0b42546 pulled.
- Relevant files/materials: plan.md sections 1-8.
- Existing changes to preserve: uncommitted iOS port work (docs/harness/*, docs/ios-design-decisions.md, src/Wino.Mail.UIKit*, src/Wino.Mail.iOS/{Composition.cs,Entitlements*.plist,Info.plist,LifecycleHooks.cs,csproj}, src/Wino.Platform.Apple/AppleApplicationIdentity.cs, src/Wino.Platform.iOS/Services/IOSMicrosoftStoreService.cs, iq/20261013-android-port-handoff.md). Edit on top; never revert.
- Verification: plan.md section 6.

## Items
| ID | Work / dependencies | Status | Done when | Result / evidence |
| --- | --- | --- | --- | --- |
| T1 | Shared core: domain, services, API token call, sign-out hook, runtime method, DI, capabilities, VM + strings, unit tests; needs: none | Finished | Tests in plan section 5 pass; Wino.Services/Wino.Core build | [T1 report](20261013-relay-live-notifications-eb032f/T1-shared-core.md): 3 shared builds ok; filters 105/105 and 16/16; full Core 1186/1 (unrelated MailRecipientHistory failure, to check on clean HEAD in T4); VM 202/202 |
| T2 | Windows head: identity store, device info, null token provider, DI, XAML; needs: T1 | Finished | Windows Debug x64 + Release gate pass, XAML format check clean | [T2 report](20261013-relay-live-notifications-eb032f/T2-windows.md): Debug x64 0 warnings; Release x64 gate 0 warnings; format-xaml -Check -Changed clean (repo-wide -Check fails on 14 untouched files); UI manual checks pending |
| T3 | Apple shared + iOS + macOS heads, entitlements, settings pages, docs; needs: T1 | Finished | Mac Debug arm64+x64 and iOS Simulator builds pass on burak-mac | [T3 report](20261013-relay-live-notifications-eb032f/T3-apple.md): Mac Debug arm64/x64 ad hoc ok, iOS Simulator Debug ok, Simulator launch clean; mirror and simulator deleted. Mac APNs key is com.apple.developer.aps-environment, added only for provisioned builds (DMG gets no pushes) |
| T4 | Integration verification and manual-check handoff; needs: T2, T3 | Blocked | Combined tests/builds pass; manual checks recorded as pending | Shared fix: RequestSync deferred until StartAsync (+ test). Relay filter 106/106 (3x stable); full Core 1187/1 (pre-existing MailRecipientHistory failure, reproduced on clean 73737c37); VM 202/202; WinUI Debug x64 and Release x64 gate 0 warnings; format-xaml -Check -Changed ok. Blocked on manual/signed checks below. Mac/iOS builds ran before the one-line shared fix. |

## Resume
- Current: T4 (blocked)
- Next: separate session (user decision 14 Oct 2026): move new-mail pushes to APNs alert pushes (priority 10 + content-available) in relay and apps, iOS Notification Service Extension, silent pushes only for flag changes; then retest Mac and iOS. Revert SynchronizerFactory.IsImapIdleTemporarilyDisabled before shipping.
- Blockers: APNs withholds background pushes after bursts (evidence in History 13:06); iOS device not attached; Windows UI check pending.
- Unfinished operations: none. Mac has a single install ~/Applications/Wino Mail.app (Debug, sandbox), mirror ~/Desktop/wt-relay.

## History
- 2026-10-13T23:06+02:00 | plan | Finished | Plan approved; record created.
- 2026-10-13T23:08+02:00 | T1 | In Progress | Shared-core agent launched (Opus, medium).
- 2026-10-13 | T1 | Finished | Shared core integrated from report.
- 2026-10-13 | T2,T3 | In Progress | Windows and Apple head agents launched (Opus, medium).
- 2026-10-13 | T4 | Pending | MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce also fails on clean 73737c37 (temp worktree D:\worktrees\wr-clean, removed): pre-existing.
- 2026-10-13 | T2 | Finished | Windows head integrated; Debug and Release gate pass.
- 2026-10-14 | T3 | Finished | Apple heads integrated; Mac arm64/x64 and iOS Simulator builds pass.
- 2026-10-14 | T4 | Blocked | Shared early-request fix + test; combined tests and Windows builds pass; manual and signed-device checks pending on user.
- 2026-10-14 | T4 | Blocked | Created ASC profile 'Wino Mail macOS Developer ID' (R3M399RA2V, MAC_APP_DIRECT, aps production, uuid cd86d212-84f3-44d1-90e2-8ae1d79700a7), installed on Mac; DMG script + csproj (WinoApsEnvironment) + docs updated. Mac Release arm64 signed Developer ID + profile: codesign verify ok, aps production, embedded profile ok; launch passed taskgated profile check, startup stopped at Keychain read -25320 (items owned by Debug signature, prompt needs user). iOS Debug ios-arm64 signed (Wino Mail iOS Development, aps development): build ok, 0 errors. No iPhone attached, so no install/push test. Mac mirror deleted.
- 2026-10-14T01:05+02:00 | T4 | Blocked | Mac Release (Developer ID + profile) live test with user: Keychain allowed, APNs gave a 32-byte production token, opt-in on, re-sign-in fixed REFRESH_TOKEN_INVALID, 'Registered relay device for platform macos', iCloud IMAP mailbox active (subscription 01a1377f). First push-test accepted, later presses 429 (1/min limiter counts every press). No push reached apsd for com.winomail.macos: relay APNs likely disabled or sandbox flag wrong for production token. Release app left running from ~/Desktop/wt-relay; user's Debug instance was closed.
- 2026-10-14T01:08+02:00 | T4 | Blocked | Mac Debug (development profile, aps development) with relay UseSandbox=true: sandbox token registered, relay status Enabled, iCloud mailbox active, 'relay test' -> Queued -> 'Relay push received: test' within 1 s, LastTestPushUtc recorded. Added Debug-only bridge command 'relay status|sync|test|on|off' and a receipt log line in AppDelegate.PushRelay.cs. Waiting for a real mailbox.changed push (needs a new mail in iCloud inbox).
- 2026-10-14T01:09+02:00 | T4 | Blocked | Real mail to iCloud: app's own IMAP IDLE synced at 01:09:11; relay push mailbox.changed (new_mail) arrived 01:09:16; push-triggered InboxOnly sync succeeded, outcome NewData. macOS end to end verified. Remaining: iOS device test (no iPhone attached), Windows UI check, Mac settings UI visual check, Mac Release with production APNs (needs relay UseSandbox=false).
- 2026-10-14T01:29+02:00 | T4 | Blocked | User request: TEMP IMAP IDLE off (SynchronizerFactory.IsImapIdleTemporarilyDisabled = true; revert before shipping). Test push now shows a 'Live notifications are working' notification (INotificationBuilder.CreateLiveNotificationsTestNotificationAsync default no-op, Apple impl). Test button: RateLimited result, test-specific texts (4 new en_US strings), 1-minute cooldown. Tests: relay/factory/runtime 82/82, VM NotificationSettings 19/19. Mac Debug rebuilt: IDLE off confirmed in log. Test push at 01:24 Queued, token valid at relay (tokenRegistered=True), not delivered after 5+ min: probable macOS background-push throttling (~6 silent pushes in 20 min). Watching 10 more minutes.
- 2026-10-14T01:37+02:00 | T4 | Blocked | Mac Debug-only 'Test notification' context items added (mail list, calendar item, task; contacts already had one). Mac notification image converter moved from NSImage to ImageIO (fixed AppKitThreadAccessException off the UI thread). Bridge: 'relay delivered|device'. Verified: notify-test mail -> Notification Center holds it (01:33:57, 01:35:13). Relay test pushes at 01:24 and 01:35 Queued, relay says token registered, none delivered (01:08 and 01:15 were). Needs relay outbox check (/admin/outbox) in the relay session. Known unrelated: sound copy to container Library/Sounds/*.aiff.tmp access denied.
- 2026-10-14 | T4 | Blocked | User asked to remove all Wino apps on the Mac: deleted 49 built bundles (bin/obj/staging in Wino-Mail, Wino-Mail-backup, .worktrees/*, wino-ios, wt-relay, Wino Releases/.staging; sources, Native frameworks and app data kept), unregistered 240 LaunchServices entries, Spotlight shows none. Fresh mirror ~/Desktop/wt-relay ready; signed Debug build waits for the winosign screen session (gone after reboots). Plan: build, ditto to ~/Applications/Wino Mail.app, delete build-folder bundles, retest.
- 2026-10-14T12:53+02:00 | T4 | Blocked | Clean redeploy: signed Debug installed as the only copy at ~/Applications/Wino Mail.app (build-folder copies deleted, LaunchServices gc). Launch: IDLE off, 32-byte token, relay Enabled, device 01a1377f tokenRegistered=True, iCloud mailbox active, notifications authorized. Relay test push 12:50:52 Queued, not delivered by 12:52:29 (0 in Notification Center). Hours after the last push, so not OS throttling; relay code unchanged since f0583c1 (22:48, before the 01:08/01:15 successes). Needs relay outbox/logs (admin key or VM access).
- 2026-10-14T13:06+02:00 | T4 | Blocked | Prod VM (ssh wino-prod) read-only: relay push_deliveries show APNs 'sent' (200) for all 12 Debug pushes 01:08-01:35 and 12:50:53; one 01:00 Release push pruned BadDeviceToken (sandbox relay). Mac apsd log (via winosign session): app-token messages received at exactly the relay send times up to 01:15:25; nothing for 01:24, 01:25, 01:29, 01:35 or 12:50:53. App token hash == relay token hash (fdfbdfe0e0d0eb04). Conclusion: APNs accepts but withholds background (priority 5) pushes after the burst; relay-side and client-side are correct. Next decision for user: relay alert push type (priority 10, + content-available) or wait for the budget to reset.
