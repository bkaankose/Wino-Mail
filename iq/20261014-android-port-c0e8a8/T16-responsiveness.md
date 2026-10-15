# T16 Android responsiveness pass (done, 15 Oct 2026)

Debug builds on Wino_Tablet_API36 (phone size via the existing `wm size 1080x2400` override, reset afterwards), Wino_Phone_API36_16KB and Wino_Phone_API23, demo seed. Screenshots `T16-*.png`.

## Fixed
- **Multi-select title:** on compact widths the ActionMode shows the count only (Material contextual bar) in a custom title view; TalkBack reads the shared `MailsSelected` sentence (content description, polite live region). Wider windows keep the sentence.
- **Font scale 2.0:** info bar action wraps under the paragraph when it would squeeze it, and the button grows instead of clipping; compose captions ("From") grow instead of breaking mid-word; reader date wraps to two lines (120 dp cap) so the sender keeps room; onboarding pill buttons grow (min height instead of fixed). Reader and editor WebViews follow the system font scale (`TextZoom`), body text was fixed at 100 %.
- **TalkBack:** clickable WinoIcons glyphs (Select, drawer folder disclosure, thread chevron) are Buttons read by their description, never the private-use glyph; folder disclosure says folder name + Expanded/Collapsed (state description); thread chevron "Expand/Collapse thread"; colour swatches "Color #…" (`Accessibility_ColorOption`); top app bar titles and the compose title are headings (`WinoToolbarTitle.SetHeading`). `uiautomator dump`: no unlabeled clickable node on the checked screens.
- **IME:** compose with the keyboard open on short windows (landscape phone, < 280 dp left): while the body has focus the bar and header step aside so body and format bar stay above the keyboard; while a recipient/subject field has focus the format bar steps aside (`T16-landscape-compose-ime*.png`). Bottom navigation hides while the keyboard is open (search). IMAP setup: portrait keeps the focused field and actions visible; landscape uses the system full-screen editor.
- **Rotation bug:** after any rotation, back from the first folder page showed an empty "Mail" placeholder. Shared `NavigationCoordinator.CanGoBackToPage()` (+1 test); Android uses it and refreshes the back state after shell navigations. Back now goes Sent → Inbox → leaves the app, with or without rotation.

## Verified unchanged
Rotation keeps inbox scroll position, open reader, compose draft text, search query, Settings page (two panes in landscape); font 1.3/2.0 rows grow; drawer, Move sheet and Settings root fine at 2.0.

## Verification
Android Debug and clean Release builds: 0 errors. ViewModels tests 227/227 (coordinator 14/14). Mac Debug arm64 and x64, iOS Simulator Debug: 0 errors. Windows (owner): Debug x64 and Release gate for the `NavigationCoordinator` change.

## Pending / notes
- Foldable hinge split: not implemented. Pixel Fold profiles exist, but `androidx.window` is not referenced and the medium layout is a LinearLayout, not SlidingPaneLayout; needs WindowInfoTracker (window-java) plus a fold AVD.
- Local search in the demo seed returns every message (pre-existing, outside T16).
- TalkBack not run with the screen reader itself; checks are by the accessibility tree.
