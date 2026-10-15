# Wino Mail for Android: design decisions

Decided on 13 and 14 October 2026; current state recorded on 15 October 2026. These decisions fix the shell, workflow, control, and background choices for the Android app in `src/Wino.Mail.Android`, its platform project `src/Wino.Platform.Android`, and the Android control libraries under `controls/`. Shared ViewModels, services, sync, storage, and the HTML reader/editor engine are unchanged by them.

Design canvases: the Material 3 canvas <https://claude.ai/artifact/NM38t5DoovdXgBR8fZAHEg> (24 boards, the current look reference) and the first canvas <https://claude.ai/artifact/NDKJ6H5EJ8FR8Qi8CqnBTc> (Decisions board A1 to A12). The owner overrides in [Material 3 pass](#material-3-pass-and-owner-overrides) win over both. Background study: [Android port investigation](android-port-investigation.md); where it differs, this document wins.

Build, emulator, and verification commands are in [Android development](harness/development-android.md). The [iOS design decisions](ios-design-decisions.md) and the [macOS design decisions](macos-design-decisions.md) apply where this document is silent; shared naming and order come from the [Wino design guideline](wino-design-guideline.md).

## Principles

1. **Native first.** C# on .NET for Android with classic Views, XML layouts, AndroidX, and Material Components. No Compose, MAUI, or Uno. Wino draws only the mail row, folder row, avatar, pills, chips, zone backdrop, info bar, and settings cards, as on Apple.
2. **Width decides the layout.** The window size class picks the layout, never the device type or orientation.
3. **Shared backend.** Views bind through explicit C# adapters to the shared ViewModels. When an Android screen needs a rule that exists only in the Mac or iOS head, move it to shared code first; never write a third copy.

## Settled decisions

| Topic | Decision |
| --- | --- |
| Package | `com.winomail.android`, minSdk 23 (Android 6.0), targetSdk 36. Apple uses `com.winomail.macos` for universal purchase; Play has no equivalent, and the id cannot change after the first Play upload. |
| Runtime | Mono: JIT in Debug, full AOT with LLVM and full trimming in Release, `android-arm64` only. `PublishAot=false`; shared code stays Native AOT-clean. |
| Size classes | Compact (< 600 dp): single pane with bottom navigation. Medium (600 to 839 dp, every phone in landscape): navigation rail, list, and reader. Expanded (840 dp and wider): the Windows layout with the pinned drawer and the mode card at its foot. Expanded also needs 480 dp of height, so phones in landscape get the medium layout. Layout resources `layout`, `layout-w600dp` (and `layout-w600dp-h480dp`), `layout-w840dp-h480dp`. |
| Modes | Mail, Calendar, Contacts, and To Do. Settings is never a mode: the avatar in the drawer header, the rail foot, or the expanded mode card opens it. |
| Look | The theme wallpaper paints behind everything, bars are translucent (blurred on API 31 and later), content sits in one rounded zone (10 dp), glyphs come from the WinoIcons fonts, text uses Roboto. Material icons only for menu, back, close, and overflow. Material 3 DayNight theme with Wino tokens; no dynamic colour. |
| New mail | FAB on compact, rail button on medium, toolbar button on expanded. The drawer keeps the Windows "New mail" row. |
| Reader commands | Compact: a 64 dp top app bar with Back, Mark unread, and the overflow; a bottom app bar with Archive, Delete, Move, and Flag and Reply as the tonal FAB (Material pass, 14 Oct). Medium and expanded: the reader's top command bar, as on Windows. |
| Mail list | A 64 dp small top app bar (menu, title, search, overflow; no subtitle) and a 48 dp chip row: Focused/Other as a Material 3 segmented button, the dropdown filter chip for sort and filters, and Select. Date groups are list subheaders. Multi-select is a contextual `ActionMode`. Swipes through `ItemTouchHelper`. There is no Sync button: pull to refresh is the only way to synchronize. Undo and status messages are snackbars. |
| Sheets and dialogs | Move to folder, filters, the daily briefing (compact), and pickers are bottom sheets. On medium and expanded widths the daily briefing is a 400 dp side sheet; its app bar action shows only through the shared entitlement rule (`DailyBriefingAvailability`). Prompts are Material alert dialogs with consequence-led buttons; undo uses a snackbar. |
| Not on phones | Hover actions, density, reading-pane options, Companion, extra windows, tray, and global hotkeys. Keyboard shortcuts remain for hardware keyboards. |
| Settings | Settings keep the Windows groups, names, and order, as a fragment stack of Wino cards and expanders over Material switches, dropdowns, and sliders. |
| Gmail sign-in | Play services `AuthorizationClient` (on-device access token, silent re-authorization, no server, no refresh token on the device). Without Play services, or for an account with a stored Google token, the shared `GmailAuthenticator` loopback flow in a Custom Tab with the existing desktop client. |
| Outlook sign-in | MSAL Android in the system browser, redirect `msauth://com.winomail.android/<signature hash>`. The broker (Microsoft Authenticator) is off until its result handling can be tested. |
| Storage | Data under `NoBackupFilesDir`; Auto Backup off. Credentials are files protected by the shared AES-GCM envelope with a key wrapped by a non-exportable Android Keystore key; no plaintext fallback. |
| Attachments | Open through the system (`ACTION_VIEW` on a `FileProvider` URI). Magika content-type classification is not on Android (`WINO_ML_UNSUPPORTED`). |
| Distribution | Google Play is the target; local testing uses APKs. Play Billing and purchases are out of v1. See [Android release packages](releases-android.md). |

## Control mapping

| Screen | Views |
| --- | --- |
| Shell | One `Activity`: `DrawerLayout`, `CoordinatorLayout` with `AppBarLayout` and `MaterialToolbar`, `FragmentContainerView`, `BottomNavigationView`; `NavigationRailView` and a pinned drawer in the medium and expanded layouts |
| Drawer | `NavigationView` hosting a `RecyclerView` of account headers, folder rows, info bar, and footer rows; mode card only on expanded |
| Folder list | `RecyclerView` with the Wino list adapter (`DiffUtil`), sticky group strips, swipes, `SwipeRefreshLayout`, FAB anchored to the zone |
| Two panes | Fixed `LinearLayout` panes on medium (list 360 dp, reader fills); fixed three-zone grid on expanded (pane 320 dp, list 420 dp, reader fills) |
| Reader | Header, `WebView` over `Wino.Editor.Core` (`controls/Wino.Editor.Android`), attachments as assist chips; `BottomAppBar` on compact. The intelligence card is not built. |
| Compose | Full-screen fragment: recipient `ChipGroup`, subject field, `WebView` body, format bar above the keyboard through the IME insets |
| Search | An inline 56 dp search bar (not Material `SearchView`) with Local/Online, filter chips, and scope; results in the same list adapter |
| Calendar | Phone: week strip and agenda list with sticky day headers. Tablet: week grid over the shared calendar layout calculator, month grid in the pane, event details zone. `MaterialDatePicker` and `MaterialTimePicker`. |
| Contacts | Letter headers and a Wino fast scroller; filter chips on phones, address books in the pane on tablets; contact card with action chips |
| To Do | Round Wino check rows; smart-view chips on phones, lists pane on tablets; detail as a third zone (tablet) or full-screen fragment (phone); add-task bar above the keyboard |
| Onboarding | The shared Windows and iOS flow: Welcome, provider, credentials (IMAP basic and advanced setup), setup progress, then the inbox. No done page; the notification permission is asked once by the first shell |

Responsiveness: every screen survives rotation and dark-mode recreation (ViewModels outlive the activity); edge to edge on every API level, the wallpaper under the bars and an 8 dp zone margin; text scaling to 200 % grows rows. Status on 15 Oct is in [Accessibility and responsiveness](#accessibility-and-responsiveness); the foldable hinge split is not implemented.

## Material 3 pass and owner overrides

On 14 Oct the shell, mail list, reader, compose, search, and Move were restyled to the Material 3 canvas: 64 dp small top app bar, 48 dp chip row, plain list subheaders, tonal FABs, snackbars, and Material 3 sheets. Calendar, Contacts, and To Do follow the same boards; the Settings, theme gallery, briefing, and notification visuals were still in progress on 15 Oct. The owner overrode the canvas where it conflicts:

- **Wallpaper stays** behind every screen, with content in the Wino zone. The canvas's surfaces without wallpaper (Decisions B5) are discarded.
- **No Sync button** anywhere, including the drawer and the account long-press menu. Pull to refresh runs the shared synchronization.
- **No dynamic colour switch** (Decisions B7): the shared layer has no such setting.
- **Settings** keep the Windows groups, names, and order.
- **Onboarding** mirrors the shared Windows and iOS flow exactly. The boards' permissions and done page, briefing toggle, and sync counts are not built.
- **Daily briefing** is a Wino Account feature and appears only through the shared entitlement path.
- **Design fidelity rule:** the Windows page and the shared layer define the features, content, and structure; the boards set only the Material 3 look. A board item that is complex or impossible from the Windows page stays simple, and items without shared support are omitted.

## Modes

| Mode | Built | Omitted or simplified |
| --- | --- | --- |
| Mail | List with load-more, Focused/Other, filter chip, swipes (Archive leading, Delete trailing), `ActionMode` multi-select, pull to refresh; reader; search; Move sheet (dialog on wider windows); compose with recipients and suggestions, attachments (SAF and photo picker), format bar, autosave drafts that survive process death | Intelligence card, search filter editor, recent searches, Move "New folder", drawer last-sync line. Compose: rewrite, find, templates, S/MIME, emoji, colours and fonts, tables, image properties |
| Calendar | Compact: week strip and agenda, month grid with dots, details full screen, editor as a full-screen dialog. Medium and expanded: Day, Week, Work week, and Month surface over the shared layout calculator, details pane, calendar pane (mini month, calendars) | Quick-event popover (an empty slot opens the composer), drag move and resize, long-press menu, calendar search, custom recurrence editor, attendee suggestions; notes are plain text; editing an event changes only Show as and Reminder, as in the shared layer |
| Contacts | List with letter headers and a fast scroller, All and Favorites chips, card, editor (full-screen dialog) with the Windows section switcher as chips | Category filters stay in the drawer; swipes, multi-select, Call, CardDAV "New address book"; the board's "Saved to" footer |
| To Do | Smart views and lists (chips on compact, pinned pane on expanded), add-task bar above the keyboard, round check rows, steps, due-date presets, detail full screen or in the pane | Remind me, Repeat, category, linked mail, Add file, Print, Email list, Flagged mail chip, bulk multi-select, swipes; suggestions are a choice sheet |
| Settings | All 34 iOS routes on their shared ViewModels; one page at a time on compact, list beside the page from 600 dp; form dialogs (alias, category, system folders, signature, account order, Wino Account sign-in and registration, sync secret); theme service with predefined, gradient, and custom themes and accent | Companion, keyboard shortcuts, backup and restore, S/MIME, and purchases are hidden through capabilities (`Tray`, `KeyboardShortcuts`, `FileBackup`, `Purchases`); theme editor surface previews |

## Background model

| Part | Decision |
| --- | --- |
| Foreground | The shared runtime (`ApplicationLifecycleCoordinator.StartAsync`) starts when the first activity starts and runs as on desktop: IMAP IDLE, sync loops, reminders. With no started activity for 10 minutes it flushes queued changes, stops, and the process ends, because the shared runtime cannot restart after a stop (`AndroidForegroundLifecycle`). |
| v1 acceptance | Initial and delta synchronization succeed through pull to refresh while the app is visible (owner decision of 14 Oct, round 3: no Sync button). |
| Pending changes | Each queued change and each move to the background enqueue the unique, expedited WorkManager work `wino-flush-queued-changes` (network constraint); `PendingChangesWorker` runs `IApplicationRuntime.FlushQueuedRequestsAsync`. The queues are in memory, so the flush helps only while the process lives. No periodic background sync worker. |
| Push relay | FCM token provider, device info, identity store, and payload handler compile and register, but are not tested or relied on. Firebase starts only in builds with `src/Wino.Mail.Android/google-services.json`; without it there is no token and `RemoteNotifications` is off. |
| Reminders | The in-process poller while the runtime runs; otherwise an `AlarmManager` wake-up for the next reminder, inexact unless `SCHEDULE_EXACT_ALARM` is granted, set again after a reboot or update. |
| Notifications | One channel per account (group "Mail accounts"), plus calendar, account alerts, and a silent background channel; mail grouped per account; the two configured shared mail actions through a `BroadcastReceiver`; taps through `INotificationResponseService`; `NotificationContentRules` for content. `POST_NOTIFICATIONS` (Android 13 and later) is asked once when the shell first appears. |

The Decisions board (A11) assumes push-triggered background sync; the owner's 14 Oct decision makes the relay compile-only for v1, as above.

## Accessibility and responsiveness

Status after the 15 Oct pass:

- **Font scale 200 %:** rows, info bars, compose captions, the reader date, and onboarding buttons grow instead of clipping; reader and editor WebViews follow the system font scale.
- **Labels:** clickable WinoIcons glyphs are buttons with descriptions, never the private-use glyph; folder disclosure and thread chevrons announce their state; bar titles are headings. Checked through the accessibility tree (`uiautomator dump`), not with TalkBack running.
- **IME:** compose on short windows moves the bar and header aside while the body has focus; bottom navigation hides while the keyboard is open in search; IMAP setup keeps the focused field and actions visible.
- **Rotation and recreation** keep the scroll position, open reader, compose draft, search query, and Settings page.
- **Not implemented:** the foldable hinge split (needs `androidx.window`).

## Deferred

Magika, S/MIME, device contacts integration, widgets, the IMAP provider catalog ("More providers"), the recurrence editor, Play Billing, and distribution scripts. Printing and PDF export stay in scope.

## Open for the owner's review

| # | Item | Recommendation |
| --- | --- | --- |
| R1 | Gmail through the system account chooser (drawn) or the Custom Tab only | Chooser when Play services exists, Custom Tab fallback |
| R2 | Outlook and Yahoo sign-in as a partial-height Custom Tab (Android 13 and later) over Wino, full-screen on older APIs | As drawn |
| R3 | Welcome copy adapts the Windows string to name the Windows PC and Mac | Confirm the wording |
| R4 | Notification permission asked at the end of setup, not at first launch | Superseded: onboarding mirrors the shared flow, so the first shell asks once |
| R5 | Decisions board items marked "Confirm" (A3, A4, A6, A8, A12) | Taken as drawn in the plan |
| R6 | Foreground runtime grace period (10 minutes, then the process ends) | Confirmed by the owner (14 Oct); implemented in T11 |

The first canvas's extra boards (T0) were not published there; the Material 3 canvas publishes all 24 boards and is the current reference.

## Project map

| Project | Target | Role |
| --- | --- | --- |
| `src/Wino.Mail.Android` | `net10.0-android` (Exe) | Head: `MainApplication`, `MainActivity`, `AndroidComposition`, navigation over the shared `NavigationCoordinator`, window size class service, fragments and XML layouts, manifest, assets linked from WinUI |
| `src/Wino.Platform.Android` | `net10.0-android` | Paths, Keystore key provider, secure item store, dispatcher, launcher, clipboard, reader runtime, presence, authentication hosts |
| `controls/Wino.Presentation.Android` | `net10.0-android` | Android twin of `Wino.Presentation.UIKit`: units, insets, WinoIcons, colours, theme surfaces, zones, backdrops, bindings |
| `controls/Wino.Mail.Controls.Android` | `net10.0-android` | Android twin of `Wino.Mail.Controls.UIKit`: mail rows and list adapter, swipes, shell rows, settings cards, avatars, pills, chips, info bar, calendar surfaces, contact and task rows |
| `controls/Wino.Editor.Android` | `net10.0-android` | `WebView` reader and editor sessions over `Wino.Editor.Core` (`WebViewAssetLoader`, `WebMessageListener` bridge with a Java fallback) |

`Wino.Platform.Android` never references presentation projects. Control logic both Apple and Android need goes to `controls/Wino.Mail.Controls.Core` or `Wino.Presentation.Core`.

## Verification

A compiled view does not establish parity. For each canvas board, manual acceptance on the phone and tablet emulators compares layout, row content, command order, and selected states against Windows, then checks light and dark, portrait and landscape, font scale, and TalkBack. See [Android development](harness/development-android.md#verification).
