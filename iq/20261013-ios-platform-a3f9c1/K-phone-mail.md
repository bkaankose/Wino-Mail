# K: iPhone Mail flow (T15)

**Worktree:** `D:\Wino-Mail\.claude\worktrees\agent-acb86f8b72debeae2` (branch `worktree-agent-acb86f8b72debeae2`, HEAD 80ca7f38). It was seeded with `seed-after-J.patch`, which `git apply --3way` staged. My edits are unstaged and new files are untracked. Nothing is committed.
- **Mac mirror:** `~/Desktop/wt-K`. Helper scripts are in `~/Desktop/wt-K-tools`: `api.sh` is the iOS API dump, and `run.sh` handles launch, bridge commands and simulator screenshots.

**Result:**
- The iOS head builds with 0 errors.
- On the iPhone 18 Pro simulator (iOS 27), the whole flow ran against a Debug-seeded local account: Mailboxes → Inbox list → reader with toolbar → back, plus edit mode, the Move sheet, search, the filter sheet, thread expansion, and delete from the reader.

## Files (`src/Wino.Mail.UIKit.Phone`)

| File | What it does |
| --- | --- |
| `Mail/MailboxesViewController.cs` | Replaces J's placeholder (deleted). It is the Mail stack root. |
| `Mail/MailboxesRows.cs` | Phone-local data source over the shell menu (see Mailboxes below). |
| `Mail/MailListPage.cs` | Root view: one `WinoZoneView` with a column and a bottom overlay. |
| `Mail/MailListPageViewController.cs` | The list page. Partials: `.List`, `.Commands`, `.Search`, `.Reader`, `.Debug`. |
| `Mail/MailRowMapper.cs` | UIKit twin of the Mac mapper. Density is fixed at Medium, because the phone has no density option. |
| `Mail/MailReaderToolbar.cs` | The reader's bottom toolbar. |
| `Mail/MoveFolderSheetViewController.cs` | The Move folder picker sheet. |
| `Mail/MailListFilterSheetViewController.cs` | The search filter editor sheet. |
| `Mail/PhoneBarItems.cs` | WinoIcons bar glyphs, command row, menu actions. |
| `Mail/Debug/PhoneMailDebugHook.cs` | `#if DEBUG` only (see Verification). |
| `Mail/Debug/ReaderStandInViewController.cs` | `#if DEBUG` only (see Verification). |
| `PhoneShellViewController.cs`, `PhoneShellHost.cs`, `PhonePageRegistry.cs`, `PhoneViewRegistration.cs` | Updated (see the sections below). |

## How the design rules are met

**Mailboxes**
- **Rows:** account, linked-account (merged) and folder rows use `WinoShellRowCell`, with unread pills and depth.
- **"More" and parent folders:** they expand in place. On folders that also open a list, the chevron area toggles expansion.
- **Live updates:** `MailboxesRows` observes item `PropertyChanged` and child collections, and coalesces them into one reload per main-loop turn. J's `ShellMenuRows` only follows collection changes.
- **Shared rules:** visibility, child, interactive, indicator and selection rules come from `ShellMenuItemRules`, and glyphs from `SpecialFolderGlyphs`.
- **Bar buttons:** the leading avatar opens Settings. The trailing New Message button invokes `IMailShellClient.CreatePrimaryMenuItem`. A briefing button appears only if `IUIKitBriefingPageFactory` is registered.
- **Pull to refresh** calls `SynchronizeAsync`.
- **Sync status strip:** a `WinoSyncBar` plus the provider's `SynchronizationDescription`, shown while `SynchronizationState.IsSynchronizing`.
- **Long press** offers the shared folder operations (`GetFolderContextMenuActions` → `PerformFolderOperationAsync`) and the account actions the Mac offers.

**Folder list**
- **Table:** a `UITableView` with a diffable source keyed by stable identity (`T:threadKey` / `I:stableId`). It is built over the shared `MailListProjection`, with the same 120 ms coalescing as the Mac.
- **Group headers:** date groups are sections with sticky `WinoMailGroupHeaderView` headers.
- **Threads:** expanded with the cell chevron or the long-press menu.
- **Header:** Focused/Other is a `WinoPivotBar` segmented control, hidden when there is only one pivot. Next to it is a filter/sort pull-down: `FilterOptions`, `SortingOptions`, and "Search filters…".
- **Bars:** the sync-disabled info bar, the Other-inbox link, "Try online search", an undo bar and a status bar floating over the bottom.
- **Other:** pull to refresh runs `SyncFolderCommand`. Empty and loading states use `WinoStateView`. Load-more fires on scroll.

**Selection and reader**
- A tap goes through `ApplyMailSelectionSnapshot`. The resulting `ActiveMailItemChangedEvent` navigates `MailRenderingPage`, which is pushed, or `ComposePage` for a draft, which opens as a full-screen sheet.
- If the open message leaves the list (delete, move or archive), the reader pops.
- Returning to the list clears the selection, so a second tap opens the message again.

**Swipes and long press**
- Leading swipe: Archive (Unarchive in the Archive folder).
- Trailing swipe: Delete, Flag/Clear flag, and More. More opens an action sheet with the remaining shared actions plus Pin.
- Long press shows a menu from `GetAvailableMailActions`, grouped at its separators, plus Pin/Unpin, a Category submenu (deferred load) and Expand/Collapse thread.

**Edit mode** (the ViewModel's `IsMultiSelectionModeEnabled`)
- Rows show round checks and multiple selection is on.
- The navigation bar shows Select All / Unselect All and Done, and the title shows the selection count.
- The bottom row has Archive, Delete, Move, Flag and More, and the tab bar is hidden.
- Leaving the list ends edit mode.

**Search**
- A `UISearchController` with Local/Online scope buttons.
- Active filters show as removable `UISearchToken`s; removing one drops that filter.
- Suggestions: "From: text", "Subject: text", Unread, Has attachments, Flagged, and Filters….
- A row shows the folder scope (current / subfolders / all), "Local · n" and a Filters (n) button.
- The criteria come from the shared `MailSearchCriteriaFactory` and `MailSearchFolderResolver`, the same flow as the Mac.

**Reader toolbar**
- Archive/Unarchive, Delete, Move, Flag/Clear flag, a Reply menu (Reply, Reply all, Forward) and More (read state, junk, print, save, source).
- Availability and titles come from `MailRenderingPageViewModel.MenuItems`. Enabled state follows `OperationClickedCommand` through `CommandBinding`, and taps execute that command.
- Move opens the list's Move sheet for the selection.

**Sheets**
- Move uses medium/large detents with a grabber; the filters sheet uses large only.
- Both go through `PhoneShellHost.PresentSheet(controller, PhoneSheetSize, dismissed)`.

**Look and feel**
- One Wino zone per page over the shell backdrop.
- The toolbar uses the default blurred appearance.
- Only WinoIcons are used.
- Hover actions are off and density is fixed.

## Registry and host

**`PhonePageRegistry`**
- `Register<MailListPageViewController>(MailListPage, ShellContent)`.
- `RegisterHost(MailRenderingPage, Push)`, `RegisterHost(ComposePage, FullScreenSheet)`, `RegisterHost(IdlePage, ShellContent)`.
- Debug only: if no controller is registered for `MailRenderingPage`, the stand-in reader is used.

**Deviation: the list is not a `Push` route.** I registered it as `ShellContent`, which J's phone shell already pushes over Mailboxes.
- `Push` maps to the coordinator's RenderingFrame kind. That would bypass `SetShellContentAsync`, `ShellContentViewModel` and the content release.
- `Push` would also leave `IsModeAvailable(Mail)` true without a shell content page.

**The list stays alive after a pop.**
- Popping back to Mailboxes does not release it: `ReleaseAsync` is overridden to ignore the pop-triggered release.
- A tap on the same folder re-pushes it through `PhoneShellViewController.ShowRetainedMailContent`. Without this, the tap would do nothing, because `NavigateFolderAsync` returns early for the already-selected folder.
- Selecting another folder or switching mode releases it normally.

**`PhoneShellHost`**
- Overrides `Push`: for a controller whose `AssociatedViewModel` is `MailRenderingPageViewModel`, it sets `HidesBottomBarWhenPushed` and calls `PhoneShellViewController.AttachReaderToolbar`.
- Adds `PresentSheet(controller, PhoneSheetSize, Action)` and the `PhoneSheetSize` enum.

**`PhoneShellViewController`**
- A `UINavigationControllerDelegate` on every stack shows the toolbar only for pages that bring items, and disposes reader toolbars once their reader leaves the stack.
- New Mail-flow hooks: `MailProvider`, `SelectedMenuItem`, `OpenFromMailboxes` (animated push), `ShowRetainedMailContent`, `Compose`, `ShowBriefing`.

**`PhoneViewRegistration`** registers `MailListPageViewController`. In Debug it also registers `PhoneMailDebugHook` (an `IApplicationLifecycleHook`) and `ReaderStandInViewController`.

## Additive infrastructure changes (`src/Wino.Mail.UIKit/Infrastructure`)

1. `UIKitPageRegistry.RegisterHost(WinoPage page, UIKitPageHost host)`, backed by a private `_hostOverrides` map.
   - It sets an idiom's host for a page whose controller another registration supplies.
   - It wins over that registration's host in whichever order the two run; host resolvers still run after it. It adds no route on its own.
2. New `IUIKitBriefingPageFactory { UIViewController Create(Action close); }`, which is optional.
   - The briefing has no `WinoPage`, so the shell presents it through this factory.

Nothing else outside the Phone project changed.

## Verification

Logs and screenshots are in `artifacts/ios-platform/K-*`, copied to `D:\Wino-Mail\artifacts\ios-platform\`.

| Check | Result |
| --- | --- |
| `Wino.Mail.iOS` Debug `-r iossimulator-arm64` | **0 errors**, 8 warnings, all existing third-party IL2104 (`K-ios-debug-sim.log`). |
| Phone project with `-p:Nullable=enable` | **0 nullable diagnostics in K files**. One CS8764 was found and fixed (`K-phone-nullable.log`). |
| Mac head Debug osx-arm64 (run because Infrastructure changed; the Mac doesn't reference it) | **0 errors, 54 warnings**, J's baseline (`K-mac-debug-arm64.log`). |

**Runtime: how the flow was driven.** The IMAP lab is not reachable from the Mac, so I used the Debug bridge:
- `seedmail` creates a local demo IMAP account: `demo@wino.test`, a closed loopback port and placeholder test credentials. It adds 8 folders (Projects/2026 under More) and 28 mails with threads, flags, unread and attachment states across several days.
- Synchronization fails fast. The list shows the shared "synchronization is failed" status bar, which is expected.

**Screenshots and what each one shows:**

| Screenshot | Shows |
| --- | --- |
| `K-01-list.png` | Inbox list: date groups, thread head with count, unread, flag, attachment, filter pull-down. |
| `K-02-mailboxes.png` | Account row with pill and indicator, folders, selected Inbox, More with disclosure. |
| `K-03-reader.png` | Stand-in reader with the toolbar. The bridge reported all six enabled from `MenuItems`. |
| `K-04-edit.png` | Edit mode: checks, Select All / Done, "2 item(s) selected", bottom row, no tab bar. |
| `K-05-move.png` | Move sheet at the medium detent; source folder and More greyed. |
| `K-06-search.png` | Search: Local/Online scope, scope row "Local · 6", Filters, online-search panel. |
| `K-07-filters.png`, `K-08-filters-dark.png` | Filter sheet, light and dark. |
| `K-thread-sim.png` | A thread expanded (22 → 25 rows). |
| `K-after-delete.png` | Delete from the reader popped back to the list (24 rows). |

Bridge checks in `out.log`:
- Re-tapping the folder after a native pop re-pushed the retained list with no reload (rows=22).

**Bug found and fixed:** row-height callbacks re-entered `UITableView.numberOfRowsInSection`, causing a stack overflow on the first folder load. `KeyAt` now asks only the diffable source.

**Not exercised (simulator automation cannot drive these):** swipe actions, the long-press menus, the Category submenu, the More action sheet, pull to refresh, search tokens and suggestions typed by hand, compose from the New Message button, Reply menu and More menu execution, the briefing sheet, Mailboxes folder and account menus, and VoiceOver. Rotation and hardware-keyboard shortcuts were also not tested; I added no keyboard shortcuts.

## Gaps and handoff

1. **M1 pages are not registered in this tree.**
   - `MailRenderingPage`, `ComposePage` and `IdlePage` need a controller registration: the base `UIKitPageRegistry`, `UIKitViewRegistration`, or a line in `PhonePageRegistry.RegisterMailPages`.
   - My `RegisterHost` calls already set their iPhone hosts. The Debug stand-in disappears automatically once a real reader is registered.
   - The real reader must be an `IViewModelHost` whose `AssociatedViewModel` is `MailRenderingPageViewModel` (`WinoViewController<MailRenderingPageViewModel>` is), so it gets the toolbar.
   - M1's composer must close its full-screen sheet itself.
2. **The briefing factory:** M1 should register an `IUIKitBriefingPageFactory`. Until it does, the button stays hidden.
3. **WinoIcons defect (controls, H): `WinoIcons.MonoImage(...)` turned into a template image shows about three times too large in bars and buttons.**
   - It goes through a `UIImageAsset` variant. J's tab-bar icons showed this (oversized tab glyphs in J's screenshots).
   - The phone works around it in `PhoneBarItems.Glyph`, which renders with `WinoIcons.Draw`. J's tab images now use it too.
   - Fix in `controls/Wino.Presentation.UIKit/WinoIcons.cs`, then drop the workaround.
4. **iOS 26/27 toolbar overflow:** separate image bar items overflowed after 3–4 items. The reader and edit toolbars therefore use one custom-view row of `UIButton`s (`PhoneBarItems.Row`) inside the `UIToolbar`.
5. **Shared contract issue (from J):** `OnNavigatedTo` and `DeactivateAsync` declare a non-null `object` parameter that is never read. The phone passes a private sentinel instead of `null!`. The proper fix is nullable parameters in Domain.
6. **`MailRowMapper` is phone-local.** If the pad (L) needs the same UIKit mapper, move it into `Wino.Mail.UIKit` instead of duplicating it.
7. **Other notes:**
   - Folder order in Mailboxes (Inbox after Trash for the seeded account) comes from the shared folder ordering, which is out of scope.
   - The Debug stand-in and seed contain English literals and are `#if DEBUG` only.
   - `iq/20261013-ios-platform-a3f9c1.md` reads as blank-filled on disk (only spaces/NULs). I did not touch it.
8. **Pending manual checks** on a simulator or device by hand:
   - Swipes and long-press menus.
   - Search tokens and suggestions.
   - Pull to refresh.
   - Compose and reply with M1's composer.
   - Real reader integration.
   - Dark mode and themes across the list, rotation, Dynamic Type, VoiceOver.
   - A real IMAP, Outlook or Gmail account.
