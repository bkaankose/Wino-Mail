# O2: Contacts and To Do on iPhone and iPad (T17, contacts and tasks part)

**Worktree:** `D:\Wino-Mail\.claude\worktrees\agent-a9e4b5fb6c02aeca5` (branch `worktree-agent-a9e4b5fb6c02aeca5`, HEAD 80ca7f38).
- Seeded with `seed-after-KLM1.patch` (staged by `git apply --3way`). My work is unstaged edits and untracked files. Nothing is committed.
- **Mac mirror:** `~/Desktop/wt-O2`. Helper scripts are in `~/Desktop/wt-O2-tools`: `build.sh`, `run.sh` (launch, bridge commands, screenshots), and `sims` (simulator UDIDs).

**Result:**
- The iOS head builds with **0 errors**. The 8 warnings are all existing third-party IL2104 (`O2-ios-debug-sim.log`).
- Contacts and To Do ran on my own iPhone 18 Pro and iPad Pro 13 (M5) simulators (iOS 27) against Debug-seeded local data:
  - Contacts: list, card and editor (cancel and save).
  - To Do: lists, tasks and detail.

## Files

**New, shared** (`src/Wino.Mail.UIKit`):

| File | What it does |
| --- | --- |
| `Views/Contacts/ContactsPageViewController.cs` (+ `.List.cs`, `.Menus.cs`) | Abstract Contacts page.<br>• List: a letter-indexed table with `WinoContactRowCell` and `WinoContactGroupHeaderView` rows and a section index. It pages on scroll, supports pull to refresh, and shows the empty and readiness (blocked) states.<br>• Search: the iPhone uses a `UISearchController`; the iPad uses `IShellSearchTarget`.<br>• Swipes: leading favourite, trailing delete.<br>• Long-press menu, a port of the Mac menu: Send mail, Edit, Favourite, Add to list (deferred), Category toggles (on/mixed), Remove from list, Delete.<br>• iPhone filter menu built from the shared contacts `ShellMenu` (Windows order).<br>• Debug commands. |
| `Views/Contacts/ContactCardViewController.cs` | The contact card. It follows `ContactsPageViewModel.SelectedContact` and shows:<br>• star, picture, name, job, source and chips;<br>• action row: Send mail, Call and FaceTime (`tel:`/`facetime:` through `IExternalLauncher`), Edit;<br>• `WinoContactFieldCard`s for Email, Phone, Address and Personal (birthday through `ContactDisplayFormatter`);<br>• Remove from list and Delete. |
| `Views/Contacts/ContactEditPageViewController.cs` | Port of the Mac editor.<br>• Sheet bar with Cancel and Save.<br>• Hero: photo menu, preview name, favourite.<br>• Section switcher that scrolls sideways.<br>• All five sections. Fields stack vertically on iPhone and sit in pairs on iPad.<br>• Swipe-to-dismiss is blocked while the editor is dirty. |
| `Views/Contacts/SheetRouteAnchorViewController.cs` | Mode-local adapter for the editor's `GoBack` (see Shell hooks). |
| `Views/Contacts/ModeBarItems.cs` | Bar, menu and swipe helpers with correctly sized WinoIcons glyphs (`Glyph`, `Tinted`, `Star`). |
| `Views/Contacts/ContactsDebugCommands.cs` | `#if DEBUG`: `seedpeople` and `seedtasks` (see Verification). |
| `Views/ToDo/ToDoPageViewController.cs` (+ `.List.cs`, `.Menus.cs`) | Abstract To Do page.<br>• Header: surface title in the accent colour, subtitle.<br>• Grouped `WinoTaskRowCell` cards with round checkboxes and group headers.<br>• Swipes: leading complete, trailing delete and important.<br>• Long-press menu, a port of the Mac menu: My Day, importance, completion, due presets, Pick a date (inline `UIDatePicker` sheet), Move to, Delete.<br>• Quick add bar pinned to `KeyboardLayoutGuide`.<br>• Filter menu: scope, important, sort, clear.<br>• Options menu: suggestions, rename, print, email, delete list.<br>• Search filters the surface (`FilterText`).<br>• Readiness panel and debug commands. |
| `Views/ToDo/ToDoTaskDetailViewController.cs` | Detail drawer.<br>• Header: checkbox, editable title, star, status line.<br>• Steps: progress, editable step rows, Add step.<br>• Schedule: My Day, due menu with presets and picker, Remind me and Repeat (marked "Soon"), Delete.<br>• Notes, Create calendar event, created line.<br>• The bulk-selection pane for a multiple selection. |
| `UIKitViewRegistration.Contacts.cs`, `.Tasks.cs` | Partial `RegisterContactsViews` (editor, Debug seed hook) and `RegisterTasksViews` (no shared registrations needed yet). |

**New, iPhone** (`src/Wino.Mail.UIKit.Phone`):
- `Contacts/PhoneContactsPageViewController.cs`: search controller, filter menu, card pushed on tap.
- `ToDo/PhoneToDoPageViewController.cs`: compact layout, detail pushed on tap.
  - It slides Lists in under itself and moves the shell's avatar to Lists.
  - It stays alive while popped back to Lists, the same pattern as K's Mail list.
- `ToDo/ToDoListsViewController.cs`: the To Do `ShellMenu` as `WinoShellRowCell` rows.
  - Rows: New list, My Day, Planned, Important, then the account, its groups and its lists, with separators.
  - Tapping a row selects it through `UIKitShellViewController.SelectMenuItem` and shows the tasks page.
  - Groups expand in place; pull to refresh synchronizes.
- `PhonePageRegistry.Contacts.cs`, `.Tasks.cs`, `PhoneViewRegistration.Contacts.cs`, `.Tasks.cs`.

**New, iPad** (`src/Wino.Mail.UIKit.Pad`):
- `Contacts/PadContactsPageViewController.cs`: the card goes in the reader column through `PadShellViewController.ShowInReader`, with an empty state.
- `ToDo/PadToDoPageViewController.cs`: the detail drawer goes in the reader column, with a close button.
- `PadPageRegistry.Contacts.cs`, `.Tasks.cs`, `PadViewRegistration.Contacts.cs`, `.Tasks.cs`.

**Edits to existing files** (only what the brief allows):
- Added `partial` to `PhonePageRegistry`, `PadPageRegistry`, `PhoneViewRegistration` and `PadViewRegistration`. `UIKitViewRegistration` was already partial.
- Added the call lines:
  - `RegisterContactsPages(); RegisterTasksPages();` in both registry constructors;
  - `RegisterContactsViews(services); RegisterTasksViews(services);` in `AddPhoneShell`, `AddPadShell` and `RegisterUIKitViews`.
- `en_US/resources.json`: four new keys, `IOS_ContactAction_Call`, `IOS_ContactAction_FaceTime`, `IOS_ToDoListsTitle` and `IOS_ContactEditor_Protocol`. The last one replaces the Mac's hard-coded "Protocol" literal. All other strings reuse existing keys.

## Registry entries

| Page | iPhone | iPad |
| --- | --- | --- |
| `ContactsPage` | `PhoneContactsPageViewController`, ShellContent | `PadContactsPageViewController`, ShellContent |
| `ContactEditPage` | `ContactEditPageViewController`, FullScreenSheet | `ContactEditPageViewController`, Sheet (form sheet) |
| `ToDoPage` | `PhoneToDoPageViewController`, ShellContent | `PadToDoPageViewController`, ShellContent |

The contact card, task detail and Lists are not `WinoPage` routes. The pages place them.

## Shell hooks and local adapters

1. **Editor back navigation (adapter: `SheetRouteAnchorViewController`).**
   - **Problem:** the shared `ContactEditPageViewModel` leaves through `INavigationService.GoBack()`. `NavigationCoordinator` pops a sheet only when it holds two or more pages; otherwise it walks the shell-content history, which would replace the mode's content (for example with `MailListPage`). Any in-place editor route (ShellContent or RenderingFrame) hits the same history problem.
   - **Adapter:** the editor is a sheet on both idioms. On first appearance it slides an invisible anchor page under itself. `GoBack` pops the editor onto the anchor, and the anchor closes the sheet through `INavigationHost.CloseSettingsAsync`.
   - **Proper fix:** in `UIKitNavigationService.TrySettingsGoBack` or the coordinator, close the sheet when its root goes back.
   - **Deviation:** the brief asked for iPad editors in place. They are form sheets instead, for this reason.
2. **iPhone To Do Lists root (adapter: `PhoneToDoPageViewController` + `ToDoListsViewController`).**
   - `PhoneShellViewController.PlaceContent` makes a non-Mail page the stack's only page, and only Mail has a menu root (Mailboxes).
   - **Proper fix:** a shell-owned menu root per mode.
3. **iPad sidebar:** no gap. L's sidebar already renders the contacts filters and the task smart views, groups and lists, with context menus.
4. **iPad reader column:** the pages call `PadShellViewController.ShowInReader` directly, because there is no `WinoPage` for the card or the detail.
   - The frame clears its page on a mode change. The pages release their card or detail and clear the frame when they release.
5. **Saved-contact selection:** the router keeps `NavigationResult` to itself, so after a successful edit the editor calls `ContactsPageViewModel.LoadAndSelectContactAsync(id)`. This is the Mac's `PendingSelection` equivalent.

## Verification

Logs and screenshots are in `artifacts/ios-platform/O2-*`, copied to `D:\Wino-Mail\artifacts\ios-platform\`.

| Check | Result |
| --- | --- |
| `Wino.Mail.iOS` Debug `-r iossimulator-arm64 -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true` | **0 errors**, 8 warnings, all existing IL2104 (`O2-ios-debug-sim.log`) |
| Warnings in O2 files | None. No null-forgiving operators and no suppressions. |
| Simulators | "O2 iPhone 18 Pro" `BFC46040-0E49-4E13-BB94-2AFF237E8416`; "O2 iPad Pro 13" `54DA3443-412F-4130-B99E-5C903DAC1671` |

**Seed data** (Debug bridge, through the shared services):
- `seedpeople` grants People on the first account and fills a local address book with 12 contacts, some with phones, birthdays and favourites, through `EnsureLocalAddressBookAsync` and `ReplaceAddressBookAsync`.
- `seedtasks` grants To Do and creates the lists "Tasks" and "Wino releases" with 6 tasks: due dates, My Day, importance, steps, notes and one completed.
- Without an account, both create a bare local demo IMAP account like K's: a closed loopback port and placeholder test credentials.

**Screenshots:**

| iPhone | iPad |
| --- | --- |
| `O2-phone-contacts-list.png`: indexed list, search, filter, new, avatar | `O2-pad-contacts-card.png`: sidebar filters, list column, card in the reader column |
| `O2-phone-contact-card.png`, `O2-phone-contact-card-dark.png` | `O2-pad-contact-editor.png`: form sheet with paired fields |
| `O2-phone-contact-editor.png`, `-editor-work.png`, `-contact-new.png` | `O2-pad-todo-detail.png`, `-dark.png`: lists in the sidebar, tasks, detail drawer |
| `O2-phone-after-cancel.png`, `-after-new-cancel.png`, `-after-save.png` | `O2-pad-todo-list.png` |
| `O2-phone-todo-lists.png`, `-todo-tasks.png`, `-todo-list-named.png`, `-todo-detail.png` | |

**Bridge results:**
- Cancel closed the editor sheet back to the card, both from the card's Edit and from New.
- Save closed the sheet and returned to the card.
- The contact menu lists the expected items.
- The task menu lists all 9 Mac items.
- Tapping a list on Lists showed that list ("Tasks", 3 rows).
- Tabs keep their titles ("To Do", "People").

**Defects found and fixed during the run:**
- Fields on iPhone were trailing-aligned.
- Card field cards were not full width.
- Action titles were truncated.
- The tab title changed to the surface title. Setting `Title` on a tab-root page renames the tab, so the pages set `NavigationItem.Title` instead.
- Shell separators rendered as type names.
- Star, close and step-delete glyphs were oversized (H's `MonoImage` and `WinoIcons.Image` in buttons). The pages now use `ModeBarItems.Tinted`.
- The iPad card and detail had a double zone.

**Not exercised (pending manual checks):**
- Swipe actions, long-press menus (only their contents were checked through the bridge) and the filter menus.
- Search typed by hand, pull to refresh, and the date picker sheet (presented through the bridge but not screenshotted).
- Typing in the quick add bar with the keyboard up, and step and title edits.
- Call and FaceTime launch (the Simulator has no phone).
- Photo change, list and category toggles in the editor.
- A saved edit actually persisting. The display name is explicit, so the given-name change was not visible.
- iPad bulk selection: there is no multi-select gesture on iOS yet, so the pane shows only with a programmatic multiple selection.
- Hardware keyboard, rotation, compact iPad width, Dynamic Type, VoiceOver.
- Real Outlook, Gmail or CardDAV data.
- Release build with warnings as errors: not run. Debug has no warnings in O2 files.
- Windows and Mac builds: not run, because no shared non-iOS code changed apart from four new en_US keys.

## Gaps and handoff

1. **K's Debug files are missing from the seed and from the main tree.**
   - Missing: `src/Wino.Mail.UIKit.Phone/Mail/Debug/PhoneMailDebugHook.cs` and `ReaderStandInViewController.cs`.
   - Cause: `.gitignore` line 20, `[Dd]ebug/`, ignores them, yet `PhoneViewRegistration`/`PhonePageRegistry` reference them under `#if DEBUG`, so a Debug iOS build of the seed fails.
   - I copied them from `~/Desktop/wt-K` into this worktree (still ignored, so they do not appear in `git status`).
   - **Fix:** rename the folder (for example `Mail/DebugTools`) or force-add the files.
2. Router fix for sheet-root back (Shell hooks item 1). Once it lands, `SheetRouteAnchorViewController` can go.
3. **H: `WinoIcons.MonoImage` and `WinoIcons.Image` render oversized in `UIButton` configurations.** Affected: the task row star, `WinoContactStyle.StarImage` and `WinoToDoStyle.IconButton` glyphs. The rows' stars are H's cells and still large.
4. **Merge note:** `resources.json` gains 4 keys at the end. Other agents also append there, so expect a trivial merge.
5. **Not ported:**
   - the Mac To Do shell-search suggestions (`ToDoPageViewController.Search`): iOS search filters the surface instead;
   - contact drag-and-drop onto lists (optional per the brief);
   - CardDAV "New address book";
   - multi-select modes.
