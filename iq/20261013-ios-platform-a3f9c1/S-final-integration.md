# S: Final integration (O1 on Q), shell hooks, calendar visuals, open items, T18 review, docs, verification

**Tree:** `D:\Wino-Mail`, branch `feature/iOS`, main working tree. Nothing is committed or pushed.
- My edits are unstaged on top of the staged tree.
- New files are untracked.
- Deleted files were removed from the index with `git rm --cached`.

**Mac mirror:** `~/Desktop/wt-S` (standard tar excludes, `WinoStoreKit2.xcframework` from `wt-J`, `Sparkle.xcframework` from `~/Desktop/Wino-Mail`).
- Tools are in `~/Desktop/wt-S-tools`: `build.sh`, `run.sh`, `step.sh` and `all.sh` (the build matrix).
- Simulators (iOS 27.0, created for S): "S iPhone 18 Pro" `3EA94F38-316D-4D61-8239-4535CDD02F64` and "S iPad Pro 13" `B9CF07B3-52BB-47DD-ABB2-27A84DB84A3D`.
- I shut down the older M2, O1, O2 and Q simulators. None was deleted.

**Result:**
- Every build passes with 0 errors.
- Tests: the only failure is the known `MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce`.
- The walk ran on both idioms, through every requested state.
- No crash, and no unhandled or managed exception, appears in the consoles.

## 1. Merged tree (O1 on Q)
- The first iOS Debug build of the merged tree had 0 errors (`S-ios-debug-sim-1.log`).
- The partial declarations in `UIKitViewRegistration.cs`, `PhonePageRegistry.cs`, `PhoneViewRegistration.cs` and `PadViewRegistration.cs` were already consistent. Nothing needed fixing.

## 2. Shell hooks (O1/O2 workarounds removed)

| Hook | Where | Replaces |
| --- | --- | --- |
| **Rendering-frame host attach**: `IRenderingFrameHost.TryShowInFrame` plus `UIKitNavigationService.AttachRenderingHost/DetachRenderingHost`, a mirror of the Mac `AppKitNavigationService`. The router offers each `RenderingFrame` page to the attached content page before the shell's frame. A host that takes a page owns it and releases it; the router still activates it. The host is cleared when the shell leaves. | `Infrastructure/UIKitShellContracts.cs`, `UIKitNavigationService.cs` | `CalendarShellHostAdapter` (deleted), `ICalendarComposeHost`, `AddCalendarShellHostAdapter` |
| The calendar base page attaches on activation and detaches on release. It takes only `CalendarEventComposePageViewController`, through `ShowComposer`: the iPhone presents a full-screen sheet (`CalendarComposerSheet`, now in its own file), the iPad shows it in place. Event details fall through to the shell frame. | `CalendarPageViewControllerBase.cs`, Phone/Pad `CalendarPageViewController.cs` | |
| **iPad sidebar content slot**: `PadShellViewController.SetSidebarContent(mode, vc)` and `ClearSidebarContent(vc)`. `PadSidebarViewController.SetModeContent` shows the content in place of the menu rows, above the shell's mode switcher. The slot is dropped when the mode changes. Contacts and To Do need none, because L's sidebar renders their menus. | `Shell/PadShellViewController.Modes.cs` (new), `PadSidebarViewController.cs` | Pushing the calendar sidebar onto the sidebar column's stack. `PadCalendarSidebarViewController` loses its own backdrop and mode switcher. |
| **Per-mode column policy**: Mail, Contacts and To Do use 320/480/360. Calendar pins the content column to (split − tiled sidebar − 340pt) so the reader column is the 340pt details zone. It is re-applied on layout and rotation. | `PadShellViewController.Modes.cs` | The page pinning and restoring widths (`CalendarPageViewController.Layout.cs` rewritten) |
| **Idle details glyph per mode**: Mail, Calendar, People and To Do, each with its filled glyph (`PadRenderingFrameHost.SetIdleGlyph`). | | The Mail glyph in Calendar |
| **Narrow toolbar rule** (`CompactContentWidth` 640, measured inside the column's safe area). | | The compact row with a period pull-down |
| **iPhone mode menu root**: a generic shell-owned `ModeMenuRootViewController` with an `IModeMenuRowStyle`. The phone shell puts To Do content over it, as Mail sits over Mailboxes. A root tap selects through the shell and pushes the retained content again. Contacts and Calendar keep their page as the root. | `Phone/Shell/ModeMenuRootViewController.cs`, `Phone/ToDo/ToDoMenuRowStyle.cs` (new), `PhoneShellViewController.cs`, `PhoneToDoPageViewController.cs` | `ToDoListsViewController` (deleted), and `AttachLists`/`ShowFromLists` in the To Do page |
| `CalendarGlyphs` deleted. Its 14 call sites now use `WinoIcons.Image` with the same signature. | | |

**Narrow toolbar rule in detail:**
- In a narrow column, the row keeps the sidebar toggle and sync, plus the page's own navigation (‹ › Today).
- The calendar range and period control move into a header at the top of the calendar zone (`CalendarPage.Header`, range over the period).
- New event stays in the sidebar.
- Search, briefing, What's New and the avatar stay over the reader column.
- In a wide column the Mac row returns: range, period control and New event.

**Calendar toolbar fix:** the calendar page now builds its leading items from `PadShellViewController.LeadingItems`. A page re-created on a period change had lost the sidebar toggle and sync.

**Debug bridge:**
- Phone: `moderoot`, `moderoot-tap N`, `stackinfo`.
- Pad: `padcolumns`; `calinfo` reports the columns, the slot and the toolbar mode.

## 3. Calendar surface (`controls/Wino.Mail.Controls.UIKit/Calendar`)
- **All-day strip:** no grey band. The zone surface shows through, with weekend shading, minor day lines and a hairline above the grid.
- **Tiles under 64pt** (`NarrowTileWidth`):
  - tighter insets, no glyph column, title font at most 11pt;
  - the title wraps by word over up to 4 lines in place of the time and location lines.
  - Week tiles on iPhone now show "Stand-up", "Dentist" and "1:1 with Alan". Overlapping 20pt tiles still break words ("Desi/gn").
- **Initial scroll:**
  - It waits for settings, then scrolls to the work-day start (working hours from the shared settings, else 8:00), as Windows and the Mac do.
  - It scrolls again when the hour height or the work-day start changes. The old offset was computed for a default canvas before settings arrived, which caused the 5:00 opening.
  - On a 13" iPad portrait the canvas can only scroll to about 6:00, because the viewport shows 6:00 to 24:00.

## 4. Q's open items
- **Draft-creation failures** (shared `MailAppShellViewModel.CreateNewMailForAsync`): any exception other than `MimePersistenceException` is now logged and shown through `IMailDialogService.InfoBarMessage(Info_DraftCreationFailed, message, Error)` on every head. This is not driven at runtime, because the seed has an alias now.
- **iPhone My Day → Lists:** verified. The stack is `ModeMenuRootViewController('Lists') > PhoneToDoPageViewController('My Day')` with `back='Lists'` (`S-phone-30-todo`, `31-todo-lists`, `33-todo-detail`).
- **General page:**
  - The iOS catalog (`SettingsPageCatalog.DeviceDescription`) gives General the existing neutral `SettingsLanguage_Description` on the root and in search.
  - The page hides its header description, so the language card says it once.
  - No new strings.
- **Manage accounts upgrade card:**
  - Title: the existing short key `WinoAddOn_UNLIMITED_ACCOUNTS_Name` ("Unlimited Accounts").
  - Description: `WinoUpgradeDescription`.
  - The purchase-channel text is a caption above the meter (`S-phone-45-settings-manageaccounts`).
- **What's New notes** are still Windows-worded. They are release content and need a user decision (per-platform notes).

## 5. T18 wiring (`src/Wino.Mail.iOS`)

| Item | State | Simulator evidence |
| --- | --- | --- |
| `UNUserNotificationCenter.Delegate` set before launch finishes; categories (`AppleNotificationBuilder.RegisterCategories`); response delegate attach; reminder server; badge update | Wired (`AppDelegate.FinishedLaunching`, `NotificationsLifecycleHook`). Authorization requests Alert, Badge and Sound. | Ran at every launch with no error. Delivery and taps not exercised. |
| Badge presenter `IOSApplicationBadgePresenter` (`SetBadgeCount`) | Registered in `IOSPlatformRegistration` | Ran through `UpdateTaskbarIconBadgeAsync`. Not visually checked. |
| BGAppRefresh and BGProcessing registration → `IApplicationRuntime.SynchronizeOnceAsync`; schedule on ready and on background | Wired (`BackgroundRefreshLifecycleHook`) | App log: "BGTaskScheduler is not available on this platform". The debugger-only `_simulateLaunchForTaskWithIdentifier` was not run: **pending on a device or under the debugger.** |
| Remote-notification registration | Wired (registration only) | App log: "no valid aps-environment entitlement" (expected unsigned). **Pending on a signed device.** |
| StoreKit purchase service start and sync | **Was missing; now wired:** `AppStoreLifecycleHook` (`#if WINO_APPSTORE`, registered in `Composition.cs`) calls `StartTransactionUpdates()` and a background `SyncOwnedPurchasesAsync()` when `IWinoAppStorePurchaseService.IsAvailable`, as the Mac `AppDelegate.AppStore.cs` does. | Ran without logged errors. Purchases are pending (no App Store record). |

## 6. Docs
- **`docs/harness/development.md`:** three applications, an iOS row, an iOS workspace line, `WinoTargetPlatform=iOS`, and an Apple-shared-layer note.
- **`docs/harness/development-ios.md`:**
  - `seedcalendar [ADDRESS]` in the seed row;
  - a Calendar commands row;
  - `stackinfo`, `moderoot*` and `padcolumns`;
  - a note on the walk order and on the bridge (lines run back to back; `settingspage` opens Settings itself).
- **`docs/ios-design-decisions.md`:**
  - Settings is a full-screen sheet on both idioms (the "to be confirmed" note is resolved);
  - a new Calendar section;
  - Calendar status merged;
  - the known-gaps paragraph replaced by the list of hooks;
  - the To Do row names the shell-owned Lists root.
- Link check: development.md 8, development-ios.md 10, ios-design-decisions.md 4, AGENTS.md 26. 0 broken.

## 7. Verification
Logs and screenshots are in `D:\Wino-Mail\artifacts\ios-platform\S-*`: 127 PNGs and 31 logs, including the consoles.

| Check | Result |
| --- | --- |
| Windows Debug x64 (WinUI) | 0 errors, 0 warnings (`S-windows-debug-x64.log`) |
| Windows Release gate (x64, win-x64, no package) | 0 errors, 0 warnings (`S-windows-release-x64.log`) |
| `Wino.Core.Tests` | 1120/1121; the 1 failure is the known `TheSameMessageInASecondFolder_IsCountedOnce` |
| `Wino.Mail.ViewModels.Tests` | 186/186 |
| `Wino.Mail.Controls.Tests` | 86/86 |
| `Wino.Core.ML.Tests` | 10/10 |
| `format-xaml.ps1 -Changed -Check` | No XAML changed ("No XAML files matched"). The full `-Check` reports 168 files, none of which differ from HEAD (`S-format-xaml-check.log`). |
| Mac head Debug osx-arm64 (ad hoc) | 0 errors, 54 warnings (baseline) |
| Mac head Debug osx-x64 (ad hoc) | 0 errors, 48 warnings |
| `WinoMail.MacOS.slnx` Debug | 0 errors, 58 warnings |
| Mac ViewModels tests | 186/186 |
| Mac Controls tests | 86/86 |
| iOS head Debug `iossimulator-arm64` | 0 errors, 15 warnings: IL2104 plus the `Wino.Platform.Apple` CS8601/CS8613 (F) on a clean build, 8 incremental |
| iOS head Release `iossimulator-arm64` | 0 errors, 15 warnings (same set) |
| iOS head Release `ios-arm64` device (`-p:CodesignKey=- -p:EnableCodeSigning=false -p:CodesignProvision=`) | 0 errors, 11 warnings. The SDK accepted it without a profile. `codesign`: "not signed at all"; arm64 executable. |
| `WinoMail.iOS.slnx` Debug | 0 errors, 22 warnings |

**Screenshots (`S-phone-*` / `S-pad-*`):**

| State | iPhone | iPad |
| --- | --- | --- |
| Welcome | 01 | 01 |
| Mail list, Mailboxes or shell | 02, 03, 04 | 02, 03 |
| Reader | 05 | 04 |
| Move | 06 | 05 |
| Reply | 07 | 07b |
| New Message | 08 | 06 |
| Calendar week, day, month | 10b, 11, 12 | 10/14, 12, 13; work week 11 |
| Event details | 13 | 14b |
| Composer (sheet / in place) | 14 | 15 |
| Quick event | 15 | 16 |
| Contacts list, card, editor, cancel | 20–22b | 20–22b |
| To Do | 30–33 | 30, 31 |
| Settings root, General, Personalization, Message list, Notifications, Manage accounts, category dialog | 40–46 | 40, 41, 42b, 43, 44, 45, 46 |
| Briefing | 50 | 50 |
| What's New | 51 | 51 |
| Light and dark Mail list and Settings root | 60–63 | 60–63 |

Phone 70–74 is the Settings-back investigation (see below).

**Walk findings that are not product defects:**
- The bridge runs a command file's lines back to back.
- `settingspage` calls `ChangeApplicationMode(Settings)`, so after `settings` it stacks a second Settings root. One extra back is then needed. Verified by stepping (`S-phone-74a…f`); this is now in the dev guide.
- Ten router backs past the Settings sheet walked the shell content history into a Calendar page while the mode stayed Mail. This came from bridge `back` only; no on-screen control does it. It is listed below.

## Consolidated pending manual checks and user actions (Q + O1 + S)

**Mail:**
- Reply and More menus, swipe and long-press menus.
- Compose Cancel and Send on a real draft.
- Choosing a folder in Move.
- The briefing button with Intelligence access.
- The iPad command row with real taps, including Open in new window and closing a pop-out (draft save).
- New Message and Reply with real IMAP, Outlook and Gmail accounts.
- The new draft-failure info bar with a real failure.

**Calendar:**
- Real gestures: tap, double-tap, long-press move and resize, range drag, swipe paging, pull-down menus.
- Composer Save (create, then ForceReload), attendee suggestions, recurrence, drop and attachments.
- Details actions: RSVP, Show as, Reminder, Delete.
- The iPad landscape toolbar row and the column policy in real rotation (headless rotation is ignored).
- Split View and Stage Manager widths.
- "More details" from the quick event and the shell `NewEventCommand` picker.
- Real Outlook, Gmail and CalDAV calendars.

**Settings:**
- Account details sub-pages.
- Wino Account sign-in and registration.
- App Store purchase.
- Theme editor pickers and the unsaved-change prompt.
- Email templates, shortcut capture, Intelligence pages, theme backdrops, checkout return.
- Confirm the full-screen sheet on iPad against the design.

**Contacts:**
- Swipe, long-press and filter menus.
- Typed search, pull to refresh.
- Call and FaceTime on a device.
- Photo, list and category toggles.
- A saved edit persisting.

**To Do:**
- Quick add with the keyboard.
- Step and title edits, the date picker.
- iPad bulk selection.
- iPhone Lists by touch: rows, group expansion, New list, pull to refresh.

**All:** Dark mode by hand, Dynamic Type, VoiceOver, hardware keyboard, rotation.

**Background and notifications:**
- BGTask refresh and processing on a device or under the debugger.
- Remote-notification registration and delivery on a signed device.
- Notification actions and badge.

**User actions:**
- Register the `com.winomail.ios` App ID and the **Wino Mail iOS Development** profile.
- Create the App Store record, then replace the placeholder App ID.
- Add the Azure redirect `msauth.com.winomail.ios://auth`.
- Create the Google iOS OAuth client and its `Info.plist` scheme.
- Run a signed device build.
- Decide on per-platform What's New notes (the current ones are Windows-worded).

**Code follow-ups (not done):**
- The `Wino.Platform.Apple` nullable warnings (F).
- L's `null!` initialisers in the Pad list.
- Shell-history back crossing modes without a mode switch (bridge-only so far; check the shared `NavigationCoordinator` history on iOS).
- Overlapping week tiles on iPhone still wrap mid-word at about 20pt.
