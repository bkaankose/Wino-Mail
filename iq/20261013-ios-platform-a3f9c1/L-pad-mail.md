# L: iPad Mail flow (Phase 3 item 4, T16)

**Worktree:** `D:\Wino-Mail\.claude\worktrees\agent-afb62daacc8e199a7` (branch `worktree-agent-afb62daacc8e199a7`, HEAD 80ca7f38).
- Seeded with `artifacts/ios-platform/patches/seed-after-J.patch` (staged by `git apply --3way`); my edits are unstaged on top. Nothing is committed.
- Mac mirror: `~/Desktop/wt-L`, with helper scripts `build.sh`, `launch-sim.sh` and `bridge.sh`.

## Files

All new files are in `src/Wino.Mail.UIKit.Pad`. J's root `PadShellViewController.cs` was removed and replaced by the `Shell/` version.

| File | Contents |
| --- | --- |
| `PadShellHost.cs` | J's host, plus `PresentPopover` / `DismissPopover`, and pop-out scenes (`RequestPopOut`, `TryConnectPopOut`, `SceneDisconnected`, activity type `com.winomail.ios.popout`). |
| `PadPageRegistry.cs` | `MailListPage` → `ShellContent`. The M1/M2 routes are written as comments (see the merge steps below). |
| `PadViewRegistration.cs` | Shell and host, `PadMailMovePresenter` / `IMailMovePresenter`, and `MailListPageViewController`. |
| `Shell/PadShellViewController.cs` | Three-column split (260 / 360 / reader). Tiles at 1008pt and wider; below that the sidebar is an overlay (`TwoOverSecondary`). In a compact width the delegate picks the top column (reader when a page is open, else the list). Also: command target, key commands, `RouteKeyboardShortcutAsync`, `NewItem`, and Debug bridge commands. |
| `Shell/PadShellViewController.Toolbar.cs` | Sidebar toggle; sync button with a spinner and tooltip from the provider; title and subtitle (folder · account · "{0} unread", reusing `UnreadBadges_Account_UnreadFormat`). Centred `UISearchBar` routed to `IShellSearchTarget` and `IShellSearchSuggestionTarget`. Briefing button with the unseen dot and a 400pt right overlay panel; What's New (hidden unless `IWhatsNewService` says to show it); avatar → `OpenSettings`. |
| `Shell/PadSidebarViewController.cs` | `WinoShellRowCell` rows for every Mac pane item type: New item, accounts, merged accounts, folders with glyphs and pills, headers, separators, More, the attention row, an account attention accessory, and generic rows for calendar, contacts and To Do. Live updates follow property and collection changes. Tapping the trailing area toggles expansion. Selection mirrors the ViewModel. Also: context menus, mail drop onto folders (`UITableViewDropDelegate`), and the `WinoModeSwitcher` card at the foot. |
| `Shell/PadSidebarContextMenus.cs` | UIMenu port of the Mac `ShellSidebarContextMenus` (folders, accounts, merged account, contact lists, task groups and lists). |
| `Shell/ShellSearchSuggestionController.cs` | Non-modal suggestion card under the field, with `Move`, `TryChoose`, `Close`, `Chosen` and `Closed`. Nothing uses it yet: on the Mac only Calendar and To Do use suggestions. |
| `Shell/PadKeyCommands.cs` | Cmd+1…4, Cmd+N, Cmd+R, Shift+Cmd+R, Shift+Cmd+F, Cmd+E, ⌫, Shift+Cmd+L, Shift+Cmd+U, Cmd+F, Cmd+, — see the keyboard rows below. |
| `Shell/ShortcutKeys.cs` | Maps stored key names to `UIKeyCommand` inputs and modifier flags. J's report mentions a `ShortcutKeys` helper, but it does not exist in the seed, so I created this one. |
| `Mail/MailListPageViewController.cs` | Main file plus partials `.List`, `.Commands`, `.Search`, `.Bars` and `.Drag`, ported from the Mac page without its reading pane. |
| `Mail/PadRenderingFrameHost.cs` | Reader column root. Routed pages sit in a zone (radius 8, reading-pane fill), with an idle glyph when empty. Has the multi-selection overlay and implements `IReaderFrameHost` (open in a new window). |
| `Mail/PadMultiSelectionView.cs` | Port of the Mac `MailMultiSelectionView`. |
| `Mail/MoveFolderPopoverViewController.cs` | Port of the Mac `MoveFolderPopover`. |
| `Mail/MailListFilterPopoverViewController.cs` | Search filter editor (port of `MailListSearchFilterPopover`). |
| `Mail/MailRowMapper.cs` | UIKit port of the Mac mapper. K probably needs the same; it is a candidate to move into `Wino.Mail.UIKit`. |

`MailListPageViewController.Drag.cs` also defines `MailDragPayload`: an NSObject local drag object, with the `CanDropOn` and `MailsFor` rules from the Mac.

## Additive infrastructure members (`src/Wino.Mail.UIKit/Infrastructure`)

**New file `UIKitShellContracts.cs`.** It ports the Mac `ShellContracts` to UIKit:
- `ShellCommand`, `IShellCommandTarget`, `IShellSearchTarget`
- `ShellSearchFieldCommand`, `IShellSearchSuggestionTarget`
- `IDailyBriefingPresenter`, `IWhatsNewPresenter`
- `IReaderFrameHost` / `IReaderFrameChild` (for the composer's "Open in new window")
- `IMailMovePresenter`, so the reader's Move can show a popover anchored to its button

M1 may define the briefing and What's New presenters too; reconcile the names at merge.

**`UIKitNavigationService.DetachRenderingPage(UIViewController)`.** It forgets the rendering page without releasing it. The frame calls it when it empties itself or when a page pops out, so the next routed page does not release that page a second time.

## Design-rule mapping

**Implemented:**
- **Toolbar row:** sidebar toggle, sync, title and subtitle over the list; search field, briefing, What's New and avatar over the reader. The search field is centred in the reader column, as in J's layout.
- **Columns:** 260 / 360 / reader. Each column has a zone with radius 8, as the brief asks; the iOS controls default to 10.
- **Backdrop:** the wallpaper covers the whole window, and the sidebar draws `WinoBarBackdropView` (blur and veil).
- **Mode switcher:** four segments, and Settings is never a mode.
- **Reading and moving:** the reader frame is in the shell. Move, the search filters and the suggestions are popovers or overlays. The briefing is a 400pt right overlay. Pop-out windows use a second scene.
- **Mail list:**
  - pivots, select-all, empty folder, select mode, and the filter/sort `UIMenu`;
  - sync-disabled bar, Other inbox link, undo bar, info bar, online search panel;
  - scope bar with Local/Online, scope and tokens;
  - date group section headers;
  - multi-select, swipe actions (from the preferences, as on the Mac), context menus with a deferred Category submenu, and drag to folders.
- **Never used:** the floating tab bar.

**Not implemented:**
- **Account switcher popover:** I could not find a Mac or Windows equivalent. Account picking stays the shared `ShowAccountPickerDialogAsync` sheet (J/M2).
- **Reader reuse across selections:** the Mac `RenavigateAsync` is not ported. Each selection routes a fresh reader page, because `UIKitNavigationService` releases the previous one.

## Keyboard

| Area | Behaviour |
| --- | --- |
| Built-in keys | Cmd+1…4, Cmd+N, Cmd+R, Shift+Cmd+R, Shift+Cmd+F, Cmd+E, ⌫, Shift+Cmd+L, Shift+Cmd+U, Cmd+F, Cmd+, |
| User Mail shortcuts | A Mail-mode shortcut with Cmd or Ctrl replaces the built-in key for its action. Other Mail shortcuts are added as extra keys. Everything is gated by `KeyboardShortcutContextPolicy`. Text input inside the reader frame counts as the Compose context. |
| Other modes | Calendar, Contacts and To Do shortcuts route through the content page's ViewModel, then `WinoAppShellViewModel.KeyboardShortcutHookForMode`. |
| While searching | ↑, ↓ and Esc go to a suggestion target. |
| Mail list page | Esc clears the selection, ←/→ collapse and expand threads, and Cmd+Z undoes the last queued action. |

## Merge steps for the orchestrator

- **Register the shared pages in `PadPageRegistry`.** The four lines are written there as comments, one each for:
  - `MailRenderingPage`, `ComposePage` and `IdlePage` as `RenderingFrame` (M1);
  - `SettingsPage` as `Sheet` (M2).

  Until then:
  - the reader column shows its idle zone;
  - the avatar, Cmd+, and the debug `settings` command are refused (`IsSettingsAvailable` is false).

  The brief says "Settings full-screen sheet", but J's registry note says Sheet on the iPad. Choose one when registering.
- **M1, reader command bar:**
  - Resolve `IMailMovePresenter` and call `TryShowMove(mails, button)` before falling back to the ViewModel command.
  - The composer implements `IReaderFrameChild` and shows "Open in new window" when `FrameHost.CanOpenInNewWindow`.
  - The briefing and What's New presenters implement the contracts above.
- **Head (J), for pop-out scenes:**
  - Set `UIApplicationSupportsMultipleScenes` to true.
  - In `SceneDelegate.WillConnect`, call `PadShellHost.TryConnectPopOut(windowScene, options)` first, and return when it reports true.
  - Call `SceneDisconnected` from `sceneDidDisconnect`.

  Until then `CanOpenPopOut` is false and the button should stay hidden.

## Verification

Logs and screenshots are in `artifacts/ios-platform/L-*`, copied to `D:\Wino-Mail\artifacts\ios-platform\`.

| Step | Result |
| --- | --- |
| Baseline iOS head build, seed only | 0 errors, 15 warnings (`L-baseline-build.log`) |
| `Wino.Mail.UIKit.Pad` with `-p:Nullable=enable` | 0 errors, 0 warnings (`L-pad-nullable.log`). This fixed 4 diagnostics, including a null passed to the shared non-nullable `OnNavigatedTo`/`DeactivateAsync`; a placeholder object is now passed, and the ViewModel ignores it. |
| `dotnet build src/Wino.Mail.iOS ... -r iossimulator-arm64 -p:WinoTargetPlatform=iOS` | 0 errors. The warnings are the existing IL2104 and Platform.Apple nullable ones (`L-ios-debug-sim.log`). |
| Mac head build | Not run. No shared or Mac code changed; only `Wino.Mail.UIKit/Infrastructure` changed, which is iOS-only. |

**Simulators.** I created my own iPad simulators, because other agents had J's iPad booted: "L iPad Pro 11" (`99C4E9F4-…`, 834×1210) and "L iPad Pro 13" (`BDE479FD-…`, 1032×1376). The console logs (`L-p11-console.log`, `L-p13-console.log`) show no exceptions.

**What I exercised.** With no account, the Mail provider opens onboarding as a sheet over the shell; the debug `dismiss` command removed it. Screenshots:
- `L-11-portrait.png`: below 1008pt, list and reader with the sidebar hidden.
- `L-11-portrait-sidebar-overlay.png`: the sidebar toggled as an overlay.
- `L-13-tiled.png`: 1032pt wide, three tiled columns with the toolbar row, New Mail row and mode card.
- `L-13-sidebar-collapsed.png`
- `L-13-search.png`
- `L-13-maillist-empty.png` and `L-13-theme.png`: `MailListPage` routed through the bridge (header, empty label, info bar), with and without the Snowflake wallpaper theme.

**Defect found and fixed.** A cross-hierarchy constraint for the briefing panel crashed the first shell launch. The panel is now pinned to the root's safe area, offset by the toolbar height.

**Not exercised (manual checks pending):**
- **Landscape:** the headless Simulator ignored `RequestGeometryUpdate`, and the Simulator app could not be scripted. The 13-inch portrait (1032pt) covers the wide, tiled layout instead.
- **Compact width:** the Split View / Stage Manager collapse below 640pt.
- **Anything that needs an account:** sidebar accounts and folders, live counts, context menus on real rows, mail rows, selection and the reader route, multi-selection overlay, swipe, Move popover, filter popover, drag and drop onto folders, scope bar searching, undo bar, sync spinner, briefing panel and What's New (no presenters are registered yet).
- **Hardware keyboard:** routing was not pressed. `padinfo` lists the generated key commands.
- **Not tested:** pop-out scenes, VoiceOver, Dynamic Type, dark mode.

The IMAP lab is not reachable from the Mac. Seeding a test account into the Simulator's database would let the list flow be checked.
