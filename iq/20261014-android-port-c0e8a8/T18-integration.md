# T18 Combined integration smoke (done, 15 Oct 2026)

First combined run of T5c, T10a, T10b and T17 on Wino_Phone_API36: fresh install with the four demo seeds.

## Fixed
- **Back crossed modes:** back from Calendar, Contacts or To Do showed the Inbox while the shell stayed in the old mode (in landscape, the rail showed Calendar around the Inbox).
  - Now back from another mode switches to Mail.
  - In Mail, back never steps onto another mode's page; with nothing left, it leaves the app.
  - Shared: `NavigationCoordinator.BackPage` (+1 test).
- **To Do step reverted (real, every time, persisted):**
  - Cause: step messages left `TaskItemViewModel.Task.Steps` stale. The next task snapshot (due date) saved the old steps.
  - Fix: `ApplyStepSnapshot`/`RemoveStep` keep the list in sync. Two tests in `TaskItemStepSnapshotTests`.
  - The fix holds after an app restart.
- **Bottom navigation and the keyboard:** the hide depended on a layout pass. It now follows the window insets (`WinoInsets.Apply(keyboardChanged)`) and hides the whole bar in every mode.
  - Checked in To Do, Mail search and Contacts search.
  - The add-task bar and the compose format bar stay above the keyboard.

## Checked without problems
- Mail: inbox, reader, compose and search.
- Calendar: agenda, month, week and the details pane.
- Contacts: card and search.
- To Do: list and detail.
- Settings from the avatar and from the mode card.
- No stale pane content after a mode switch.
- Phone portrait, landscape and tablet; light and dark.
- Logcat: no app exceptions.

## Follow-ups
- My Day uses the UTC date (shared, not changed). Tasks leave My Day at UTC midnight.
- Still open: the calendar segment scrolls in landscape, and the rail clips To Do on short phones.

## Verification
- **Android Debug:** 0 errors; only NU1902/NU1903 warnings.
- **Tests (MacOS):** ViewModels 239/239, Controls 95/95, Core with the 5 known failures.
- **Mac and iOS builds:** Mac Debug arm64 and x64, and iOS Simulator Debug: 0 errors, no new warnings.
- **Owner:** Windows Debug x64 and the Release gate.
- **Screenshots:** `T18-*.png`.
- The emulator was reset and killed.
