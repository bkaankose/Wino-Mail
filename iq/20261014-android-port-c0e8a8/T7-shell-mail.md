# T7 Shell and Mail in three width classes (done, 14 Oct 2026)

## Files
- Head `src/Wino.Mail.Android`:
  - `Views/Shell/`: `AndroidShellController`, `ShellFragment`, `ShellMenuAdapter`, `ShellMenuActions`, `ModePlaceholderFragment`.
  - `Views/Mail/`: `MailListFragment` (+ `.List`, `.Commands`, `.Search`, `.Filters`), `MailListController`, `MailReaderFragment`, `MailMovePresenter`, `MailRowMapper`, `AndroidSenderPictureProvider`.
  - Layouts `fragment_shell` in `layout`, `layout-w600dp` and `layout-w840dp-h480dp`, plus the list and reader layouts.
- New `controls/Wino.Editor.Android`.
- New package `Xamarin.AndroidX.SwipeRefreshLayout` 1.2.0.3 and one string, `Android_Shell_OpenNavigation`.

## Shell
- **Compact:** drawer (header avatar opens Settings), blurred bottom navigation, New mail FAB; the reader covers the shell.
- **Medium:** rail (New mail, four modes, avatar at the foot; it scrolls on short phones), list 360 dp, reader pane and the folders drawer.
- **Expanded:** pinned 320 dp pane with the mode card (four modes and Settings), list 420 dp and the reader.
- **Rule change:** expanded also needs at least 480 dp of height, so phones in landscape get the rail (canvas A3).
- Calendar, Contacts and To Do show mode placeholders. Settings and compose show `PendingPageFragment`.

## Mail
- **List:** T6 adapter with load-more; `MailListController` (page lifetime) owns projection, selection and reader messages.
  - Pull to refresh and the Sync action both call the provider's `SynchronizeAsync`, as the Windows button does.
  - Swipe: Archive on the leading side, Delete on the trailing side.
  - Long press starts an ActionMode (Archive, Delete, Move, Flag, More, Pin).
  - Focused/Other, filter sheet or popup, info bars, empty and loading states.
- **Reader:** NestedScrollView with header, banner, attachments and the WebView body.
  - Compact: a BottomAppBar with Reply anchored.
  - Pane: a Windows-style command bar.
- **Search:** inline row (not Material SearchView): Local/Online, filter chips, scope; same adapter.
- **Move:** a sheet on compact, a dialog elsewhere.
- **Not built:** the intelligence card and the daily briefing.

## Editor.Android
- **Session:** mirrors the WebKit session; each load is a fresh generation served by `WebViewAssetLoader`.
- **Remote content:** `shouldInterceptRequest` enforces the remote-content policy, with an empty 403 when it is blocked.
- **Links:** `shouldOverrideUrlLoading` cancels navigation and launches the link (`NavigationRequested`).
- **Bridge:** `winoMail` `WebMessageListener` (feature check, origin and main frame checked), else `addJavascriptInterface`; generation id required.
- **Reader:** the reader session mirrors WebKit's and adds `MeasureContentHeightAsync`.
- **T8:** the editor session is left to T8.

## Shared moves
- `MailListSelectionSnapshotBuilder` (Controls.Core): the Phone, Pad and Mac lists use it.
- In ViewModels:
  - `MailReaderCommandLayout`: UIKit and Mac readers.
  - `ShellModeActivator`: UIKit and Mac shells.
  - `ShellMenuRowPresentation`: Pad sidebar.
  - `MailDebugSeed` (`#if DEBUG`).
- `reader.js` and `editor.js` now post to `window.winoMail`.
- `AppleMailDocumentBuilder` has a new `useCommandKey` parameter.
- `WinoBarBackdropView` measures to 0 unless it gets an exact size.

## Verification
- **Android Debug:** 0 errors, no warnings from my code (the existing Pkcs NU1902/1903 remain).
- **Tests:** Controls 89/89, ViewModels 209/209, with new snapshot and layout tests. Core (MacOS): 1215 pass, the 5 known failures.
- **Mac and iOS builds:** Mac Debug arm64 and x64, and iOS Simulator Debug, all with 0 errors.
- **Emulators:** demo account seeded with `am start -n com.winomail.android/com.winomail.android.DebugCommand --es command seedmail` (Debug only; sync fails fast on a closed port).
  - Checked: list, reader, drawer, folders, ActionMode, search, filters, Move, New mail, modes, Settings, back, rotation, dark mode.
  - Fixed: back stopped working after a drawer closed while the shell was leaving.
  - Screenshots: `T7-phone-*`, `T7-tablet-expanded-*`.

## Gaps
- **T8:** compose and the editor session.
- **T9:** Settings pages and the theme service.
- **T10:** replace the mode placeholders.
- **Open:** intelligence card, briefing, search filter editor, TalkBack.
- **Real account:** initial and delta sync untested.
- **Windows handoff:** Debug x64, Release gate and tests.
