# T8 Compose (done, 14 Oct 2026)

## Files
- **Editor:** `controls/Wino.Editor.Android/AndroidHtmlMailEditorSession.cs`.
- **Head code** (`Views/Mail/Compose/`): `ComposeFragment` (+ `.Recipients`, `.Attachments`, `.Format`), `ComposeBridge`, `ComposeSuggestionAdapter`, `ComposeRecipientInput`, `ComposeHeaderScrollView`, `ComposeStyle`.
- **Layouts:** `fragment_compose`, plus `compose_recipient_row`, `compose_attachment_tile`, `compose_suggestion_row`, `compose_format_button`, `compose_format_divider`, `compose_link_dialog`.
- **Resources:** `values/dimens_compose.xml`, `values/styles_compose.xml`.
- **Pickers:** `Infrastructure/AndroidDocumentPicker` (SAF documents, photo picker) backs `AndroidDialogService.PickFilesAsync`. `AndroidActivityResults` gains `StartActivityForResultAsync`.
- **Route:** ComposePage is a RenderingFrame page: full screen on compact widths, in the reader pane otherwise.
- **Debug:** `seedrecipients` command.

## Editor session
- **Port:** the WebKit session over `AndroidWebViewSession` and `SessionOperationQueue`.
- **Caret:** list and indent commands keep the caret by text offset; Chromium otherwise moves it to the block start.
- **Keyboard:** the keyboard connection restarts around each command.
- **Pasted images:** images pasted or sent by the keyboard arrive through `OnReceiveContentListener` and are inserted inline.
- **`ComposeBridge`** (kept in `PageEntry.State`):
  - holds the ViewModel's hooks;
  - on pause, reads the editor body, then saves the draft;
  - a recreated view re-renders that body.
- **Saving and closing:** autosave runs 3 s after a change. Close and Back save, then close. Discard asks first (Discard/Cancel).

## Shared moves
- **`ComposeRecipientRules`** (ViewModels): parsing, complete-address check, suggestion threshold and delay, suggestion filtering, list expansion. 16 tests.
- **`EditorLinkRules`** (Editor.Core): link dialog kind, initial URL, URL normalization, command args. 6 tests.
- The UIKit and AppKit recipients and `EditorDialogs` now use both.

## Restyle readiness
- Compose layouts and C# contain no visual literals. Sizes, radii, alphas and glyph sizes are in the compose dimens and styles; colours are `wino_*` tokens.

## Verification
- **Builds:**
  - Android Debug: 0 errors and no new warnings.
  - Mac Debug arm64 and x64, and iOS Simulator Debug: 0 errors.
- **Tests:** ViewModels 225/225, Controls 95/95, Core 1215 passed with the 5 known failures.
- **Wino_Phone_API36:**
  - New mail, Reply, Forward.
  - Suggestions, comma commit, chip remove.
  - Bold, list and indent while typing.
  - Attach and remove.
  - Draft survives Home plus a process kill.
  - Discard, Close and Back.
  - Rotation keeps the body; landscape uses the pane.
  - Dark mode.
  - Send to server reaches the shared retry, and the failure bar shows the server error.
- **Screenshots:** `T8-phone-*.png`.

## Gaps
- **Send:** the shared ViewModel allows only Send to server for a local draft, so the real send request was not reachable with the closed demo port. I hide the "saving" spinner once the upload has failed.
- **Not built:** rewrite, find, templates, S/MIME, emoji, colours and fonts, table, image properties, "Don't suggest".
- **Rotation:** uncommitted recipient text is lost.
- **Landscape:** the phone keyboard leaves almost no room for the body.
- **Not tested:**
  - The tablet; another agent's Wino_Tablet_API36 was running.
  - Pasted images, the alias menu, TalkBack.
- **iOS:** the iOS chip shows the address instead of a suggested name for an unknown address.
- **Host load:** at a load average near 29 the emulator ANR'd on the inbox before compose opened; I rebooted it. The T7 inbox redraws continuously while idle.
- **Windows (owner):** Debug x64 build, Release gate and tests for the shared changes.
