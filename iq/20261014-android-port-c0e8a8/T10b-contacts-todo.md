# T10b Contacts and To Do (done, 15 Oct 2026)

The Windows pages are the source of truth for content; the boards set only the Material 3 look.

## Files
- **Head, `Views/Contacts/`:**
  - `ContactsFragment`: list, chips, search, menu, FAB.
  - `ContactCardFragment`, `ContactEditorFragment`.
  - `ContactsListAdapter`, `PeopleViews`, `ContactsAndToDoRoutes`.
- **Head, `Views/ToDo/`:** `ToDoFragment`, `ToDoListAdapter`, `TaskDetailFragment`, `TaskDatePicker`.
- **Head layouts:** `fragment_contacts`, `fragment_contact_card`, `fragment_contact_editor`, `fragment_todo`, `fragment_task_detail` and `people_state_panel`.
- **Head resources:** `styles_people_tasks.xml` and `tasks_add_bar_background`.
- **Head edits (additive):**
  - `AndroidPageRegistry`: the two placeholder lines were replaced by `RegisterContactsAndToDoPages()`.
  - `AndroidNavigationService`: new `ShowDetailAsync` and `CurrentDetailKey`, for a non-routed detail in the rendering frame.
  - `DebugCommandActivity`: new `seedcontacts` and `seedtasks` commands.
- **Controls:** `WinoContactRowView`, `WinoIndexScroller` (the fast scroller), `WinoRoundCheckbox` and `WinoTaskRowView`. Tokens are in `wino_tokens_people_tasks.xml`. `WinoInsets.ApplyKeyboardLift` is new.
- **String:** one new key, `Android_ToDo_Lists`.

## Screens
- **Compact:** the list fills the shell. The card or detail covers the shell, with Back. The editor is a full-screen dialog on every width.
- **Medium and expanded:** the card or detail sits in the reader pane, with Close for To Do. On expanded, the pinned pane shows the filters and lists, so the chip row is hidden.

## Shared moves
- `ContactDisplayFormatter.FormatPostalAddress` (Domain); the Mac and iOS cards now use it.
- `TaskRowPresentation.Describe` and `DetailSummary` (ViewModels); the Mac and iOS lists and the iOS detail now use them.
- `ModeDebugSeed` (ViewModels, `#if DEBUG`); the iOS `seedpeople` and `seedtasks` now call it.

## Simplified or omitted
- **Editor:** the board's "More fields" became the Windows section switcher, shown as chips. The board's "Saved to" footer was dropped, because Save to is the first field, as on Windows.
- **Contacts:**
  - Category filters stay in the drawer, not the chips.
  - Not built: swipe actions, multi-select, Call, and CardDAV "New address book".
- **To Do:**
  - Remind me, Repeat, category, linked mail and Add file are left out. Windows shows them as "coming soon" or they have no shared support.
  - Print and Email list are left out: they are placeholders on Windows.
  - No "Flagged mail" chip, no multi-select bulk pane, no swipes.
  - The suggestions pane is a choice sheet.

## Verification
- **Android Debug:** 0 errors and no new CS or CA warnings.
- **Tests (MacOS):** Core 1229 passed with the 5 known failures; ViewModels 228/228; Controls 95/95.
- **Mac and iOS builds:** Mac Debug arm64 and x64, and iOS Simulator Debug, all 0 errors with no new warnings.
- **Emulator (Wino_Phone_API36_B), Contacts:**
  - Seeded data.
  - Filter chips (All, Favorites), the fast scroller, the card, an edit and save that persisted, and a new contact save (destination picker and shared validation bar).
- **Emulator, To Do:**
  - Smart views, the Lists chip, adding a task (list picker) with the bar above the keyboard, complete and uncomplete, a step toggle, a due-date preset, and the detail in the pane.
- **Sizes and themes:** phone portrait, landscape (medium) and tablet size (`wm size`, reset afterwards), light and dark.
- **Screenshots:** `T10b-*.png`.
- **Not exercised:** long-press menus, search, photo change, pull to refresh, TalkBack.

## Gaps
- **Bottom navigation and the keyboard:** on compact widths the bottom navigation rides above the keyboard (shell behaviour). The add-task bar sits above both.
- **Step reverted after a due change:** a step toggled just before a due-date change came back unchecked once. A second toggle persisted. Suspected cause: the shared save snapshot. It has not been reproduced since.
- **Popup menus:** they use the same plain style as Mail's popups.
