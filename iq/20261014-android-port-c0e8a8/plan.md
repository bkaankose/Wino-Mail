# Wino Mail for Android: implementation plan

Status: **Approved 14 Oct 2026** with the owner's changes (section 1, "Owner decisions of 14 Oct, round 2"). Branch `feature/Android` (from `feature/iOS` at `932a2790`). Inputs: [handoff](../20261013-android-port-handoff.md), [investigation](../../docs/android-port-investigation.md), the Android canvas (Decisions board read 14 Oct), the [iOS record](../20261013-ios-platform-a3f9c1.md) as the template.

## 1. Settled (not reopened)

- .NET for Android, classic Views with **XML layouts**, AndroidX + Material Components. No Compose, MAUI or Uno.
- minSdk 23, targetSdk 36. Mono full AOT + LLVM + trimming for Release, `PublishAot=false`; shared code stays Native AOT-clean.
- Layout by window size class: compact (< 600 dp) single pane, medium (600–839) rail + list + reader, expanded (≥ 840) Windows layout with pinned drawer and mode card. Mail, Calendar, Contacts, To Do; Settings is never a mode (avatar opens it).
- Wino look: wallpaper behind everything, blurred bars on API 31+, one rounded zone (10 dp), WinoIcons glyphs, Wino rows and settings cards. Settings keep the Windows groups, names and order.
- Business rules stay in Domain/Services/ViewModels/Controls.Core. When an Android screen needs a rule that only exists in the Mac or iOS head, **move it to shared code first**; never write a third copy.
- English strings only in `en_US/resources.json`. No commits or pushes.

Owner decisions of 14 Oct 2026:

- **Gmail sign-in:** Google Play services `AuthorizationClient` (on-device access token, silent re-authorize, no server, no refresh token on the device). Devices without Play services fall back to the shared `GmailAuthenticator` loopback flow, opened in Custom Tabs, with the existing desktop client.
- **Push relay and FCM:** write the Android pieces (token provider, device info, identity store, payload handler) so they compile and register, but do not test or depend on them. **v1 acceptance: the app works and initial plus delta synchronization succeed from the Sync button.**
- **Windows:** no Windows builds or tests from this session; the owner runs them. This plan lists the exact commands.
- **IMAP lab:** not used on this Mac. Real-account checks use the owner's accounts.
- **Registrations** (Entra Android redirect, Google Android OAuth client, Firebase, keystore, package id in Play) are done by the owner later. Until then Outlook and Gmail sign-in cannot be verified; IMAP can.
- **Docs** are part of the work (section 6).

Owner decisions of 14 Oct, round 2:

- **Distribution:** Google Play is the target, but local testing uses APKs. Nothing exists on the Play side yet; Play Billing and purchases are out of v1; distribution scripts are deferred.
- **Magika is out** on Android (`WINO_ML_UNSUPPORTED`); attachments open directly through the system (`ACTION_VIEW` on a `FileProvider` URI).
- **Missing screens are designed on the Android canvas first** (Fable 5.1, medium effort) before the matching implementation: item T0.
- **Auto Backup is off** (`android:allowBackup="false"`).
- Real-account IMAP checks use an iCloud test account the owner signs in with on the emulator after implementation.

## 2. Open decisions (my recommendation first)

| # | Decision | Recommendation |
| --- | --- | --- |
| O1 | Canvas items marked "Confirm" (A3, A4, A6, A8, A12) | Accept as drawn: bottom navigation on compact, rail on medium, mode card on expanded; FAB for New mail on compact; Focused/Other toggle group; multi-select as ActionMode; Calendar/Contacts/To Do per the Mac parity boards |
| O2 | Landscape phones | Two panes (medium layout), because width class decides, not orientation |
| O3 | Phone calendar default | Agenda list with week strip, as drawn; day grid later |
| O4 | Package id | `com.winomail.android`. Mac and iOS share `com.winomail.macos` for Apple universal purchase; Play has no equivalent, so a "macos" id on Android only confuses. Cannot change after the first Play upload |
| O5 | Distribution for v1 | Settled: Play target, APK for local testing, no billing, scripts deferred |
| O6 | Deferred features (D8) | Settled for Magika (out). Also deferred: S/MIME, device contacts, widgets. Keep printing/PDF. Reminders: inexact alarms, exact when `SCHEDULE_EXACT_ALARM` is granted |
| O7 | Data location (D9) | Settled: Auto Backup off; data under `NoBackupFilesDir` |
| O8 | ABIs | Release `android-arm64`; Debug the host emulator ABI (`android-arm64` on this Mac, `android-x64` on Windows). No 32-bit |
| O9 | Screens not on the canvas | Settled: designed on the canvas first (T0) |
| O10 | Delegation | Main session orchestrates Opus 5.5 medium subagents with non-overlapping scopes, worktrees when builds collide, reports under `iq/20261014-android-port-c0e8a8/` |

## 3. What `feature/iOS` already delivers (re-verified 14 Oct)

Done: `RegisterWinoViewModels`, `NavigationCoordinator`, `NotificationResponseService`, `ActivationRouter`, `ApplicationLifecycleCoordinator`, `IGoogleAuthorizationCodeReceiver`, `IApplicationRuntime.SynchronizeOnceAsync` / `SynchronizeAccountInboxAsync`, MSAL Broker Windows-only, `controls/Wino.Presentation.Core` (plain `net10.0`, bindings + control rules + tokens), shared theme catalogue, glyph maps, `CalendarSurfaceLayoutCalculator`, push relay shared core.

Still Apple-only but portable (moved in T2): the AES-GCM `WMKP` envelope in `AppleSecretProtector`, and `AppleFileService`, `AppleConfigurationService`, `AppleStatePersistenceService` (no Apple APIs, Apple-only project).

Roadmap items still open that Android touches (moved when the Android screen needs them, not up front): context menu/flyout models (#10, #26–29, #45, #48, #49), dialog rules (#40, #47, #53–58), `SettingsPageCatalog` rest (#36), navigation fixes (#14, #18, #19).

## 4. Project map

| Project | Target | Content |
| --- | --- | --- |
| `src/Wino.Mail.Android` | `net10.0-android` app | `MainApplication`, single `MainActivity`, `AndroidComposition`, fragments per Windows page name with XML layouts (`layout`, `layout-w600dp`, `layout-w840dp`), navigation and dialog services, manifest, assets (fonts, wallpapers, What's New linked from WinUI like the Mac) |
| `src/Wino.Platform.Android` | `net10.0-android` | Platform adapters (section 5); no presentation references |
| `controls/Wino.Presentation.Android` | `net10.0-android` | View binding adapters over `Presentation.Core`, WinoIcons typeface, zone and wallpaper backdrop, theme mapping, insets |
| `controls/Wino.Mail.Controls.Android` | `net10.0-android` | Mail/folder rows, list adapter over `MailListCollection`, settings card/expander, badges, avatars, info bar, chips, calendar surfaces |
| `controls/Wino.Editor.Android` | `net10.0-android` | `WebView` reader and editor sessions over `Editor.Core` |
| `build/Wino.Android.targets`, `WinoMail.Android.slnx`, `WinoMail.Android.code-workspace` | | Build interface, same shape as iOS; `WinoTargetPlatform=Android`, never host-selected |

## 5. Platform adapters (`Wino.Platform.Android`)

- Paths: `FilesDir`, `CacheDir`, `NoBackupFilesDir`; `AllowLegacyDataMigration=false`.
- `ISecretProtector`: shared AES-GCM envelope with an Android Keystore key (non-exportable, AES-256-GCM). No plaintext fallback.
- Credential stores, DAV store, Google token store (for the loopback fallback): protected files, atomic replace, same as Apple.
- Outlook: `IOutlookAuthenticationHost` with MSAL Android, redirect `msauth://<package>/<signature hash>`, `BrowserTabActivity` in the manifest, current Activity as parent; broker through Microsoft Authenticator when installed.
- Gmail: an Android `IGmailAuthenticator` that uses `AuthorizationClient` when Play services is available and the account has no stored refresh token; otherwise the shared `GmailAuthenticator` with an Android loopback receiver (Custom Tabs). Background calls re-authorize silently; a resolution request marks the account for sign-in again. Spike in T5 confirms the account address and `HttpListener` on Android.
- Dispatcher, launcher (`ActionView`, Custom Tabs), clipboard, attachments (`FileProvider`, SAF save), reader runtime check (WebView package), metadata, capabilities (no tray, hotkeys, windows, startup, Store; Printing and PdfExport true).
- Notifications: channels per account, `POST_NOTIFICATIONS` (33+), actions through a `BroadcastReceiver`, through the shared builder policy.
- Background: runtime `StartAsync` while an Activity is started, stop after a grace period; WorkManager only flushes pending changes; reminders via `AlarmManager`.
- Relay (compile-only): FCM `IPushTokenProvider`, `IPushRelayDeviceInfo`, identity store, `FirebaseMessagingService` handing payloads to the shared handler. Fix the "lowercase hex" token wording in `IPushTokenProvider`.

## 6. Documentation

New: `docs/android-design-decisions.md`, `docs/harness/development-android.md` (toolchain, emulators, build/run/logcat, F5, verification), `docs/releases-android.md` (APK/AAB, signing, 16 KB check). Updated: `AGENTS.md` (table, routing, verification rows), `docs/harness/development.md`, `docs/releases.md`, `README.md`, `scripts/README.md`, `docs/harness/implementation-rules.md` where Android differs, `.gitignore` allow lines, `WinoMail.Android.code-workspace`.

## 7. Items and order

| ID | Item | Needs |
| --- | --- | --- |
| T0 | Canvas boards for the missing screens (onboarding and account setup, event/contact/task editors, month view, Move and briefing sheets, notification layouts, dark variants), phone and tablet; Fable 5.1 medium subagent; owner review | none |
| T1 | Build interface: `Android` in `Directory.Build.targets` guard, `build/Wino.Android.targets`, Android condition for `Wino.Core.ML` and the two ViewModel projects, slnx, workspace; portable graph compiles for `net10.0-android` | none |
| T2 | Shared moves: AES-GCM envelope protector with key provider; file, configuration and state persistence services out of `Wino.Platform.Apple`; Mac arm64/x64 and iOS Simulator builds green | T1 |
| T3 | `Wino.Platform.Android` foundation: paths, Keystore protector, credential stores, dispatcher, logging and Sentry, configuration, capabilities, metadata | T2 |
| T4 | Head skeleton: application, activity, composition, navigation and dialog services, Welcome/onboarding; launches on the phone and tablet emulators | T3 |
| T5 | Authentication: MSAL host, Play Services Gmail with loopback fallback, IMAP/CalDAV setup pages. **Prototype gate:** IMAP account signs in, initial sync, Sync button delta sync on the emulator; Outlook/Gmail checks wait for the owner's registrations | T4 |
| T6 | `Presentation.Android` and `Mail.Controls.Android` (rows, list adapter, cards, zone, icons, bindings) | T1 |
| T7 | Shell and Mail in three width classes: drawer, bottom nav/rail/card, folder list, reader WebView, search, swipe, Move and filter sheets | T5, T6 |
| T8 | Compose (`Editor.Android`, recipients, attachments, drafts, send) | T7 |
| T9 | Settings pages, account sheets, dialogs, theme service | T7 |
| T10 | Calendar, Contacts, To Do (phone and tablet) | T7 |
| T11 | Background and notifications; relay/FCM compile-only pieces | T5 |
| T12 | Release: Mono AOT `android-arm64` publish (APK; AAB build check only, no Play upload or scripts), 16 KB alignment check on the 16 KB emulator, API 23 emulator launch, Native AOT spike record (installs NDK) | T7 |
| T13 | Documentation (section 6) | T4 |
| T14 | Final integration and verification (section 8) | all |

T6 can run beside T2–T5; T8, T9, T10 and T11 run in parallel after T7/T5.

## 8. Verification

On this Mac (done in this work):
- `dotnet test` for `Wino.Core.Tests`, `Wino.Mail.ViewModels.Tests`, `Wino.Mail.Controls.Tests` after shared changes (known failure `MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce` is pre-existing).
- macOS Debug arm64 and x64, iOS Debug Simulator builds after shared or Apple changes.
- Android Debug build and run on `Wino_Phone_API36` and `Wino_Tablet_API36`, light and dark, portrait and landscape; Release Mono AOT publish; 16 KB image launch; API 23 launch.
- Manual: IMAP account sign-in, initial and delta sync by the Sync button, read, compose and send, settings. Screenshots under `iq/20261014-android-port-c0e8a8/`.

Handoff to the owner:
- Windows Debug x64 build, Release gate, XAML format check (commands in the final report).
- Outlook and Gmail sign-in after the registrations; relay/FCM; physical device; Play Console.

A build is not proof of runtime behaviour; anything not run is reported as pending.

## 9. Owner actions (not blocking T1–T4)

Entra: Android platform entry for the package with the debug and release signature hashes. Google Cloud: Android OAuth client (package + SHA-1, debug and release). Firebase project and `google-services.json` (relay later). Release keystore. Play Console when distribution starts.

## 10. Owner decisions of 14 Oct, round 3 (Material 3 pass)

- Design reference: Material 3 canvas https://claude.ai/artifact/NM38t5DoovdXgBR8fZAHEg: 64 dp small top app bar, 48 dp chip row (Focused/Other segmented button + filter chip), plain list subheaders, Material 3 components everywhere.
- **No Sync button anywhere**; synchronization is pull to refresh only. v1 acceptance: initial and delta synchronization through pull to refresh.
- **Wallpaper stays** behind every screen with content in the Wino zone; the canvas's "surfaces without wallpaper" assumption is discarded.
- **Nothing beyond the shared layer**: board items without shared implementation are omitted (e.g. QR import, Wi-Fi backfill, toggles without settings).
- No UI work for screens whose boards are not published yet (To Do, task detail, Contacts, contact/event editors, briefing, Settings, theme gallery, notifications).

- Onboarding mirrors the shared Windows/iOS flow exactly (Welcome → provider → credentials → setup progress → inbox); the boards' permissions/done page, briefing toggle and sync counts are not implemented. The daily briefing is a Wino Account feature and appears only through the shared entitlement path.

- **Design fidelity rule (owner, 14 Oct):** the Windows page is the source of truth for content and structure. When a board shows something that is complex or impossible to do from the Windows page, keep it simple: follow what the shared layer has, with only small additions where needed, and stay close to the Windows UI. The boards decide the Material 3 look, not the feature set.
