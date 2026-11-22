# Wino Mail for Linux: master backlog

Branch `feature/1216-linux-gtk`, issue #1216. Consolidated on 2026-11-20 from surveys 01 to 08, the root and Linux `AGENTS.md` files, `docs/harness/development-linux.md` and `docs/macos-design-decisions.md`. Nothing was built for this document; contradictions between surveys were resolved by reading source (see "Survey corrections").

The surveys (`S0n`), the spike projects and the UI test helpers named below were working files of the planning agents, kept outside the repository; they are not part of it.

Stack: GTK 4 + libadwaita (minimum 1.8) through Gir.Core 0.8.1, WebKitGTK 6.0, plain `net10.0`, Native AOT gate kept. Target: Ubuntu + GNOME on Wayland.

## Status

Updated 2026-11-22. Every package of phases 0 to 11 has landed on `feature/1216-linux-gtk`: spikes and the Debug-action harness, foundation, shell, mail list, folders and reader, composer and editor, dialogs, settings, accounts and sign-in, Calendar, Contacts, To Do, intelligence surfaces, and app lock, notifications, startup, desktop integration, S/MIME, onboarding and What's New. After them landed the port of the `main` 2.2.15 merge (MP-01 to MP-15), the switch to the Windows Classic icon set (IC-01), and two final passes over every mode with their fixes. Voice control (phase 12) was dropped (Q6). PO-01 ran as a partial audit only (no unnamed interactive widgets found). The Debug and Release builds of the Linux projects end with 0 warnings.

The rest of this document is the plan as written before the work; package details describe the plan, not always the result. The developer guide is [Linux development](harness/development-linux.md).

Released as a beta for amd64 as a `.deb`, an Arch package (Omarchy, AUR `wino-mail-bin`) and a Fedora RPM, with an in-app update check and telemetry in packaged builds ([Linux release guide](releases-linux.md); owner answers to Q8 and Q12). The tiling desktop profile and the Omarchy theme (TP-01) are on `feature/1216-omarchy`; their decisions start the [Linux design decisions](linux-design-decisions.md). Not done yet: the rest of the Linux design-decisions document, and the trimmed and Native AOT publish runs against a lab account (PO-03 §3). The Wino Account checkout address for Linux waits for Q5. `scripts/release/release.ps1 mark 1216` is pending on the Windows machine.

**Skipped on Linux.** The platform gaps in [section 4](#4-skipped-on-linux): Windows widgets, jump list and taskbar identity, Windows Hello (PIN lock only), store purchases (Wino Account channel instead), Mica and Acrylic, tray icon and Companion flyout, dock badge, composer auto-correct, window position, presentation and do-not-disturb detection, attachment security check, the legacy Outlook client, native Google and Apple sign-in SDKs, list and thread animations, the semantic-zoom jump list and print header and footer text. Deferred: push relay and live notifications, the compact navigation rail (full collapse instead), voice control and global hotkeys, sandbox specifics. Accepted deviations: no event summary popover and a New Event-only slot menu (CA-06), no timed to all-day conversion by drag (CA-08), no do-not-disturb check for sounds (PL-06), badge rows hidden (ST-05), Move up and Move down for task reorder (TD-05).

### Open handoffs to shared code

Found on Linux, caused in shared code, and not fixed on this branch (owner rule: no shared bug fixes from the Linux work). Apply on `main` and check on Windows and macOS.

| Id | Defect | Cause and fix idea |
| --- | --- | --- |
| A-01 | After a crash during a write, startup fails ("not a completed version-210 database", or "attempt to write a readonly database") | `DatabaseService.ReadLifecycleAsync` opens the database `ReadOnly`, which cannot roll back a hot journal. When `<db>-journal` exists, open it read-write once first. Linux mitigates it in `Infrastructure/DatabaseJournalRecovery` |
| B-14 | To Do drawer edits made in quick succession lose reminder, repeat and priority locally (the server has them) | `ToDoPageViewModel` clears pending state per run and applies an earlier deferred server state over newer edits. The fix (Linux commits `0929fc93`, `3fa0af1d`, reverted) keeps pending state until the last queued edit settles |
| B-01 | Calendar search fills its rows with occurrences of one long series; single events drop out | `CalendarService.SearchCalendarItemsAsync` orders every occurrence `StartDate DESC LIMIT n`. Return one row per series (`RecurringCalendarItemId`), the nearest occurrence |
| B-07 | Calendar order in the sidebar changes after a calendar is hidden and shown again | Shared calendar grouping (`GroupedAccountCalendarViewModel`) re-inserts at the end |
| B-20 | "New Address List" is bold, "New Address Book" regular | `ShellMenuRowPresentation.KindOf` treats only `NewAddressListMenuItem` as a NewItem row |
| B-23 | Every CalDAV calendar and task list reports the same deletions after one list changed | `Wino.Providers.Dav` delta attributes deletions to every collection |
| A-18 | Mail previews run block elements together ("Remote contentVisible image") | Shared preview text extraction joins block elements without a separator |
| ED-07 | Sent mail carries duplicate inline image parts in `multipart/related` | Shared draft MIME build |
| PL-05b | Send with Sign on throws `ArgumentNullException` when the alias has no stored certificate | The composer turns Sign on for any certificate; Send signs only with the alias's stored thumbprint |
| MP-05 | A task created from a mail has no due date on the server; an event created from a mail invites the original sender | Shared create-from-mail flows |
| AC-09 | Debug builds write the authorization URL with the login hint (an e-mail address) to `wino.log` | Shared `MsalLogForwarding`; redact the URI |
| MP-01 to MP-04 | A wrong CalDAV path on the right host is accepted; moving People back to the server leaves an extra "This device" address book; the provider picker needs a valid address first | Shared DAV discovery (well-known fallback) and account setup ViewModels |
| KB-H1 | Keyboard: the shared shortcut actions have no junk, next or previous item, print or zoom, and Mail has no Forward default, so Linux keeps these as fixed keys (`LinuxKeymap`) and seeds Forward as Ctrl+L head-locally (`ShortcutSeeds`) | Add `MoveToJunk`, `NextItem`, `PreviousItem` and `Print` to `KeyboardShortcutAction` with defaults per platform, and a Mail Forward default, in `KeyboardShortcutService`; then Linux drops its fixed copies and the seed |
| CA | After Edit series the first occurrence is missing until the next sync; CalDAV saves only Free for Show as; CalDAV events offer no Respond menu (`IsLocked`) | Not rechecked after the `main` merge. Reminders now reach the server as VALARM (fixed on `main`), and RSVP from the invitation card sets `PARTSTAT` |

Also recorded: leaving a composer always saves the draft again (new server UID); To Do cannot delete a CalDAV list on servers whose task collection may hold events; an RSVP for an IMAP and CalDAV account sends no iTIP reply; Empty folder asks no confirmation; the What's New header button never shows for a version without release notes.

**Pending builds on the other machines.** The branch adds English strings to `resources.json` only: `Composer_TableAddRow`, `Composer_TableRemoveRow`, `Composer_TableAddColumn`, `Composer_TableRemoveColumn`, `Composer_TableDelete`, `ImapSetup_CardDavValidationFailedTitle`, `Linux_KeyringUnavailable_Title`, `Linux_KeyringUnavailable_Message`, `Linux_StartupFailed_Title`, `Linux_AppLock_Description`, the `Linux_Shortcuts_*` and `Linux_SingleKeyShortcuts_*` keyboard strings and `Linux_AppLock_Hint`. The Windows Debug and Release builds, the Android Debug build and the macOS and iOS builds are pending for them.

### Owner manual checklist

Agents could not produce these on Wayland (no key injection, no pointer, no portal file choice, no real OAuth):

- **Keyboard:** the configurable shortcuts and fixed keys (Ctrl+U, Delete, Shift+Delete, Ctrl+Shift+G, Ctrl+F, Ctrl+comma, Ctrl+Q, Ctrl+1 to 4, Alt+Left, F9, Ctrl+S in the template editor), shifted digits and a non-Latin layout, the shortcut recorder, Menu key and Shift+F10, type-ahead in menus and pickers, IME preedit in search, recipients and the contact editor. The keyboard-first keys ([design decisions](linux-design-decisions.md#keyboard-first-navigation-22-november-2026)) with real keys on Hyprland and GNOME: F6 and Shift+F6 through every mode, the pane line appearing on keys and leaving on a click, Escape from reader, drawer and search, Enter and Right into the reader, Alt+Down and Alt+Up held down, F1, F5, Ctrl+W in the main window, a pop-out and a dialog, the zoom keys on a German and a French layout, the single keys with the switch on and off (letters still type in every entry and the composer), and Alt+letter mnemonics in alerts and form dialogs.
- **Composer:** typing and Ctrl+B/I/U/K/Z/Y/F/H in the body, recipient entry with Enter, Tab, comma and Backspace, chip drag and right click, suggestions with the arrows, Ctrl+Enter, image paste from another app, Nautilus drag onto the drop zones, spelling suggestions, emoji chooser, colour pickers, alias From selector, Orca.
- **Pointer and touch:** right click and long press on rows, tokens, attachments and calendar items; the hover bar; swipe by touchpad and touch; pane and divider drags; drag of mail onto folders, of contacts onto lists, of tasks and lists, and inside drag lists; submenus by hover.
- **Portals and files:** file pickers for attachments, pictures, backup export and import, theme wallpaper, profile picture, To Do export and import, Save all; print to a real printer; Files "Open With" for `.eml`, `.ics` and `.vcf` with the desktop entry installed.
- **Sign-in:** Outlook and Gmail in the browser, restart without a prompt, remove and add again, revoke and fix from the attention row (the Windows app's Gmail must stay unaffected); a real OAuth IMAP provider; an Exchange tenant; Wino Account sign-in with e-mail, Microsoft, Google and Apple and the entitled intelligence states.
- **Runtime fixes not rechecked:** sidebar folder highlight after Move (A-04), Save of an e-mail template while the main window is active (A-06), folder dialog buttons (A-09, A-10), reader subject after a dialog (A-14), Contacts sidebar scroll (B-18), the Current time line row (B-12), the quick event closing on a period change (MP-10).
- **Lab cleanup:** the empty calendar "TD01 renamed" on the lane-B account disappears when that lease is released and leased again.

## Conventions

Path prefixes: `H:` = `src/Wino.Mail.Linux/`, `P:` = `src/Wino.Platform.Linux/`, `EG:` = `controls/Wino.Editor.WebKitGtk/`, `PG:` = `controls/Wino.Presentation.Linux/` (new), `CG:` = `controls/Wino.Mail.Controls.Linux/` (new), `W:` = `src/Wino.Mail.WinUI/`, `M:` = `src/Wino.Mail.MacOS/`, `CA:` = `controls/Wino.Mail.Controls.AppKit/`, `PA:` = `controls/Wino.Presentation.AppKit/`, `VM:` = `src/Wino.Mail.ViewModels/`. `S0n` = survey n.

Sizes: S = under about 300 new lines, M = about 300 to 900, L = over 900 or one inseparable subsystem. Planning: S by Opus inline; every M and L gets its own Fable plan first. Implementation by Opus.

Build budget per package: one Debug build for the running-app checks and one Release build (the gate, 0 warnings). A package that needs a third build is too big; split it in its plan.

Every package's verification implicitly includes: Debug build, Release gate, AT-SPI tree check of the new widgets (each interactive widget has an accessible label), a light and a dark screenshot through the Debug actions of TH-01, and a resize to 1360x860, 1008x700 and 640x700. The "Verify" line lists only what is specific. Lab accounts are leased (`scripts/lab/linux/lab.sh lease`), IMAP/SMTP + CalDAV/CardDAV only.

Concurrency: at most two implementing agents. "Parallel" names the packages that touch disjoint files. Four files are merge hotspots that almost every package edits by one or two lines; F-01 and F-05 restructure them so the edits never collide:

| Hotspot | Rule set up by |
| --- | --- |
| `H:LinuxComposition.cs` (service registration) | F-05: per-area partials `H:Infrastructure/LinuxViewModelRegistration.<Area>.cs` |
| `H:Infrastructure/GtkNavigationService.cs` routes | F-05: `H:Infrastructure/LinuxPageRegistry.<Area>.cs` partials |
| `H:Infrastructure/GtkDialogService.Dismissed.cs` | F-07: one partial per dialog group `GtkDialogService.<Group>.cs`; a package deletes only its own methods from `Dismissed.cs` |
| `P:LinuxPlatformRegistration.cs` capability record | F-01: one named argument per line, alphabetical, so capability flips merge cleanly |

Architecture decisions taken here (binding for the plans):

1. **Two new Linux-only control projects, as on macOS**: `controls/Wino.Presentation.Linux` (twin of `Wino.Presentation.AppKit`: bindings glue, icons, style, weak events, live objects, list and menu helpers, about 1.5k lines) and `controls/Wino.Mail.Controls.Linux` (twin of `Wino.Mail.Controls.AppKit`: mail row, shell row, mode switcher, settings rows and binder, info bar, chips, contact picture, token box, mini calendar, calendar surface, task card). The head keeps pages, navigation, dialogs and composition. This also gives two agents disjoint files.
2. **Views stay plain classes with `Root`**, built in code. No XML, no Blueprint. GObject subclassing is allowed only where spike SP-01 or SP-06 passes under Native AOT; every package has a no-subclass fallback.
3. **Bindings reuse `Wino.Presentation.Core.Bindings`** (`BindingScope`, `PropertyBinding`, `CommandBinding`) with thin GTK extensions; no hand-rolled `PropertyChanged` filters in new views.
4. **Shell**: `Adw.OverlaySplitView` (resizable sidebar with drag handle, remembered width, toggle) with a mode host; list and reader in a persisted split that turns into push navigation when narrow. Mode switcher card at the sidebar foot (Mail, Calendar, Contacts, To Do, Settings), as on Windows and macOS.
5. **Settings is its own window** (Ctrl+, and the Settings segment), as on macOS; catalog, groups, page names and card order identical to Windows (`SettingsCatalog`). Recorded as open question Q4 because Windows hosts Settings in the shell.
6. **Styling follows macOS Revision 3**: without a wallpaper or gradient theme the panes are libadwaita-native and edge to edge; the rounded Wino zones and the backdrop appear with a Wino theme. Every Wino concept glyph comes from the WinoIcons font; Adwaita symbolic icons only for GNOME chrome (back, close, menu, window controls).
7. **Editor**: Linux gets its own copy of the command and selection-state mapping inside `EG:` (no lift into `Wino.Editor.Core`).
8. **Account setup is address-first only** (`AddressFirstAccountSetup: true`), as on macOS.

Survey corrections (checked in source):

| Survey claim | Fact |
| --- | --- |
| S01 "about 45" and S07 "about 26" dismissed dialogs | `GtkDialogService.Dismissed.cs` has 38 dialog methods |
| S02 "Adwaita symbolic icons are the right Linux answer, no WinoIcons bundling" | Overruled by the owner decision: the Wino icon font is used (F-03) |
| S06 "S/MIME missing on macOS, capability false" | macOS has it: `MacSmimeCertificateService`, `Smime: true` in `M:Composition.cs`. Windows uses `X509Store(CurrentUser)`, which .NET supports on Linux. S/MIME is therefore a Linux package (PL-05), not a skip |
| S02 "Snooze: confirm" | `MailOperation` has no snooze. Not a feature on any platform |
| S01 "mailto route may not exist" | `ActivationUriClassifier` and `MailToUri` handle `mailto:` already |
| S03 "lift the editor mapping into Core first" | Not allowed (owner decision 7 above) |
| S02 "extract Windows-only helper rules into shared code in the same change" | Not allowed; Linux mirrors the macOS re-implementation in the head |
| S01 head "about 2,100 lines" | 1,907 in the head, 834 in the platform project, 330 in the editor project; 3,055 with docs and project files |
| S07 "minimum libadwaita 1.6" | Owner decision: 1.8 minimum, so `Adw.Dialog`, `Adw.Spinner`, `Adw.ToggleGroup`, `Adw.WrapBox`, `Adw.InlineViewSwitcher`, `Adw.ShortcutsDialog`, `AccentColor` need no version gate |

## 1. Summary

**State when planned (2026-11-20).** A proof of concept of about 3,050 lines in three projects. Solid and kept: `Wino.Platform.Linux` (XDG paths, libsecret protection key, dispatcher, launcher, text clipboard, `Gio.Notification` for new mail, registration), the application lifecycle (`Adw.Application`, single instance, `wino://` activation), the WebKitGTK reader session, and the mail list patterns (append splice, selection snapshot, coalesced rebuild). Partial or throwaway: the shell (Mail only, full-rebuild sidebar, fixed panes), navigation (three routes), dialogs (3 real, 38 dismissed), theme (stub), reader chrome (labels only). Missing entirely: bindings glue, icon font, composer and editor session, search, context menus, drag and drop, settings, account setup and every sign-in, Calendar, Contacts, To Do, app lock, startup integration, What's New, onboarding. Trim and Native AOT publish work for the PoC.

**Totals.** 117 work packages: 24 S, 82 M, 11 L (115 without the two voice packages that wait for Q6), plus 7 spikes outside the repository. 93 packages (M and L) need a Fable plan; 24 are planned inline. Per phase: see "Package totals" at the end of section 3. Blocked on the owner: AC-09 (Q2), AC-10 (Q1), AC-14 (Q5), VC-01 and VC-02 (Q6).

**Line estimate.** macOS equivalent: 57.0k (head) + 12.8k (`Wino.Mail.Controls.AppKit`) + 1.0k (`Wino.Presentation.AppKit`) = about 71k lines including Voice and Companion. Linux estimate from the surveys: foundation and shell 6k, mail list, folders and reader chrome 8k, composer and editor 5k, dialogs 2.5k, settings and accounts 14k, OAuth and Wino Account 3k, Calendar 7k, Contacts and To Do 6k, platform services and late features 4k: **about 50k to 58k new lines of C#**, plus about 2k of CSS and project files. Voice control (open question Q6) would add about 4k.

## 2. Phase 0: spikes and the test harness

Spikes run as throwaway projects in the scratchpad (outside the repository), each with its own `.csproj` that references the Gir.Core 0.8.1 packages (and, for SP-05, the repository's `Wino.Editor.Core` and `Wino.Editor.WebKitGtk` projects by absolute path). Set `PublishAot` inside the spike's project file, never on the command line (`NETSDK1124`). Each spike reports: the code that worked, the `dotnet publish` warning count, and the outcome of running the AOT binary. Three agent runs: SP-01+SP-02+SP-07 (lists), SP-03+SP-04+SP-06 (drawing), SP-05 (editor). No more than two at once.

| ID | Exact question | Pass criterion | Fail criterion and fallback | Decides |
| --- | --- | --- | --- | --- |
| SP-01 | Can a `[Subclass<GObject.Object>]` item wrapper (source generator from `GirCore.GObject-2.0.Integration`; first confirm a 0.8.1 package exists on nuget.org) hold a managed reference, live in a `Gio.ListStore`, and be read back in `SignalListItemFactory` bind, in a Native AOT binary? | AOT publish adds 0 trim or AOT warnings from the spike; the AOT binary shows a `Gtk.ListView` of 50,000 items, scrolls end to end, survives 10 forced `GC.Collect()` cycles without a crash or a lost item, and frees the wrappers after the store is cleared (weak-reference count returns to 0) | Any IL2xxx/IL3xxx warning from generated code, a runtime type-registration failure, or the package missing at 0.8.1. Fallback: the PoC pattern (`Gtk.StringList` of keys + key-to-row dictionary); no `TreeListModel`, folders stay a keyed flat list | F-04 item model; SH-04, CT-01, TD-02 list models |
| SP-02 | Does `Gtk.ListView.SetHeaderFactory` show section headers when the model is a `Gtk.SortListModel` with a section sorter (`Gtk.CustomSorter` with a managed compare delegate) over the SP-01 items, or over a `StringList` when SP-01 fails? Do headers stay correct after `Splice` appends and middle inserts? | Headers render for 3 or more sections, stay correct after 20 appends and 20 middle inserts, the scroll position does not jump on append, the sorter delegate survives GC, AOT binary runs | `GtkSectionModel` is not reachable without implementing an interface in managed code, or headers go stale. Fallback: header rows inside the model (PoC), plus an overlay label for the sticky top header | ML-02 date groups, CT-01 letters, TD-02 task groups |
| SP-03 | Can the bundled `WinoIcons*.ttf` (mono) and `WinoIcons*Color-Light/Dark.ttf` (COLR) be registered for the process only, and render a private-use glyph in a `Gtk.Label` through a CSS `font-family`? Try `Pango.FontMap.AddFontFile` on the default `PangoCairo` font map first, then `FcConfigAppFontAddFile` through `LibraryImport` before the first Pango use | Mono glyph follows `currentColor` (normal, selected, disabled, dark); the colour glyph renders in colour and switches Light/Dark font on `Adw.StyleManager` change; nothing is written to `~/.local/share/fonts`; works in the AOT binary and in a fresh process with an empty font cache | Glyph shows as tofu, or the colour font renders monochrome. Fallback order: install into `$XDG_DATA_HOME/fonts/wino-mail` at start, then render glyphs to `Gdk.Texture` with SkiaSharp (already referenced) | F-03 and every view |
| SP-04 | Is `Gtk.DrawingArea.SetDrawFunc` + Cairo fast enough for the calendar surface, with event tiles as pooled child widgets in a `Gtk.Fixed` over it, moved per frame from `Widget.AddTickCallback`? Scene: week view at 1440x900, 24 hour rows, 150 tiles, continuous horizontal translation and vertical scroll | Frame time p95 at or under 16.7 ms on a GPU session and at or under 33 ms under software rendering (WSL, headless), no managed allocation per frame above 1 KB, the draw and tick delegates survive 10 forced GCs, AOT binary runs. Text through `Pango.Layout` (needs explicit `PackageVersion` entries for Cairo, Pango, PangoCairo: shared touch T2) | Slower than the limits, or delegates collected. Fallback order: translate one pre-rendered `Gdk.Texture` (Skia or Cairo image surface) per rail unit in a `Gtk.Picture`; then the custom snapshot widget of SP-06 | CA-03, CA-04, CA-05, CA-02, coverage band in AC-15 |
| SP-05 | Does the shared `editor.js` run in WebKitGTK through `AppleMailDocumentBuilder.BuildEditorAsync(useCommandKey: false)` with the existing `winoMail` script message handler, round trip included? | In the AOT binary: `ready` arrives; `WinoEditor.setContent` then `getBodyContent` returns the same HTML; typing produces `contentChanged` and `selectionState` (deserialised with `EditorJsonContext`); `exec('bold')` toggles `selectionState.bold`; an `applicationShortcut` message arrives for Ctrl+Enter while the web view has focus; pasting a PNG from `Gdk.Clipboard` and dropping an image file on Wayland both reach `editor-images.js` (inline image present in `getBodyContent`); Enchant spell check underlines a misspelt word when `WebContext` spell checking is on | Any round-trip step fails. Per step: paste fallback `Gdk.Clipboard.ReadTextureAsync` + `insertImage`; drop fallback a capture-phase `Gtk.DropTarget` on the page; shortcut fallback a capture-phase `Gtk.EventControllerKey` on the web view | ED-01, ED-07, ED-08, ED-11 |
| SP-06 | Can managed code supply measure and allocate through `Gtk.CustomLayout` (no subclass), and, only if SP-01 passed, can a `[Subclass<Gtk.Widget>]` override `snapshot` under AOT? | `CustomLayout` positions 200 children from a managed callback, re-allocates on resize without warnings, delegates survive GC, AOT binary runs. Snapshot override is a bonus result, recorded either way | `CustomLayout` callbacks cannot be supplied or crash under AOT. Fallback: `Gtk.Fixed` with positions recomputed on `notify::width`/`height`, wrapping rows through `Adw.WrapBox` | ED-03 token box, ML-08 tile line, CA-03 tile placement, RD-02 command bar reflow |
| SP-07 | Do `Gtk.DragSource` and `Gtk.DropTarget` work with a plain string payload (`Gdk.ContentProvider.NewForValue` of a `GObject.Value` string such as `app.winomail.mails:<token>`) and an in-process static payload, on Wayland, under AOT? Does `Gtk.DragIcon` accept a `Gtk.WidgetPaintable`? | Drag from a `ListView` row to a `ListBox` row: `:drop(active)` styling shows, the drop handler receives the string, the drag icon shows a custom widget, the list autoscrolls; AOT binary runs | Value marshalling fails. Fallback: `Gdk.ContentProvider.NewForBytes("text/plain", ...)` with the same token | ML-10, CT-04, TD-05, ED-03 chip drag, ED-07, ST-04 and DL-02 drag lists |

### TH-01 · Debug test harness (GActions) · S

- Scope: Debug-only application actions on `app.winomail.Mail`, reachable with `gdbus call --session --dest app.winomail.Mail --object-path /app/winomail/Mail --method org.freedesktop.Application.ActivateAction <name> "[<param>]" "{}"`: `debug-screenshot` (path, optional accessible name of a widget; `Gtk.WidgetPaintable` -> `Gtk.Snapshot` -> `Native.GetRenderer().RenderTexture` -> `Texture.SaveToPng`; null-check the renderer and retry on the next frame), `debug-resize` (width, height through `SetDefaultSize`), `debug-theme` (`light`, `dark`, `system` through `Adw.StyleManager.ColorScheme`), `debug-dump-tree` (path; role, accessible label, CSS classes, allocation, visible, sensitive for every widget of every window), `debug-quit`. All compiled out of Release (`#if DEBUG`), all refused unless the process was started with `--debug-actions`. S08.
- Not allowed: the xdg-desktop-portal Screenshot method (it wedges the portal backend), `org.gnome.Shell.Screenshot`, RemoteDesktop input.
- Mirror: `M:Infrastructure/MacDebugBridge.cs` (Debug-only commands and window snapshots).
- Files: new `H:Infrastructure/Debug/DebugActions.cs`; one call in `H:LinuxApplication.cs`. No script goes into the repository; the AT-SPI Python helpers stay outside the repository, in the agent's scratchpad.
- Deps: none. Parallel: all spikes (no repository overlap).
- Verify: each action against the running PoC with a leased lab account; PNG is client area only and matches the window size; `python3 -I` AT-SPI script finds the app node by GApplication id and clicks a folder row; a Release build contains no `debug-` action (`gdbus introspect`). Later packages extend it: SH-06 adds `debug-shortcut` (dispatch a configured shortcut, because AT-SPI cannot synthesise keys on Wayland), SH-07 adds a window selector argument.

## 3. Work packages

Format: `### ID · title · size`, then Scope (survey rows), Mirror (Windows and macOS reference files), Shared (ViewModels and services consumed), Files (Linux files created or modified), Deps, Parallel, Shared touch (T-numbers from section 7, or none), Verify.

### Phase 1: foundation

### F-01 · Control projects, bindings glue and the view base · M

- Scope: create `PG:` and `CG:` (`net10.0`, `WinoTargetPlatform=Linux`, Release gate on), add them to `WinoMail.Linux.slnx`, each with an `AGENTS.md`. In `PG:`: GTK binding extensions over `BindingScope` (`BindText` for Label and Editable, `BindVisible`, `BindSensitive`, `BindCssClass`, `BindActive` two-way for Switch/CheckButton/ToggleButton with an applying guard, `BindCommand` for Button and `Gio.SimpleAction`, `BindSelected` for DropDown/ComboRow by stable item id), `WinoWeakEvent`, `WinoLiveObjects`, `WinoA11y.Label/Description` (the one place that calls `Gtk.Accessible.UpdateProperty`), main-loop helpers (coalesced idle, throttle timer). In the head: `WinoView<TViewModel>` (per-page DI scope, `Dispatcher`, `Bindings`, `ActivateAsync`, `ReleaseAsync`, `HasPendingWork`, released guard, messenger registration helper) and `PageScopes`. Reformat the capability record to one argument per line. S07 rec. 1, 3, 14.
- Mirror: `PA:WinoWeakEvent.cs`, `PA:WinoLiveObjects.cs`, `M:Infrastructure/WinoViewController.cs`, `M:Infrastructure/PageScopes.cs`.
- Shared: `Wino.Presentation.Core.Bindings.*`, `IDispatcher`, `IWinoLogger`.
- Files: new `PG:Wino.Presentation.Linux.csproj`, `PG:AGENTS.md`, `PG:Bindings/*.cs`, `PG:WinoWeakEvent.cs`, `PG:WinoLiveObjects.cs`, `PG:WinoA11y.cs`, `PG:MainLoop.cs`; new `CG:Wino.Mail.Controls.Linux.csproj`, `CG:AGENTS.md`; new `H:Infrastructure/WinoView.cs`, `H:Infrastructure/PageScopes.cs`; modify `H:Wino.Mail.Linux.csproj`, `P:LinuxPlatformRegistration.cs`, `WinoMail.Linux.slnx`.
- Deps: none (SP results not needed). Parallel: TH-01. Must finish before any other F package.
- Shared touch: T3 (solution, guidance map rows), T4 (check `build/Wino.Linux.targets` covers the new projects).
- Verify: the PoC still starts and shows folders, list and reader; `MailReaderView` ported to `WinoView` and bindings as the proof (header labels update when another message is selected); live-object count for reader cells is stable after 50 selections (dump through a Debug action).

### F-02 · Style, tokens and CSS · S

- Scope: `WinoStyle` for GTK: CSS generated from `WinoTokens` (spacing, radii, zone gutter, the 16-step avatar palette as `.wino-avatar-N`), zone and surface classes (`.wino-zone`, edge-to-edge versus floating, per macOS Revision 3), density classes, unread pill, chip, group header, selection pipe; loaded from an embedded resource at application priority; libadwaita named colours only (`@accent_bg_color`, `@card_bg_color`, `@window_bg_color`). Replaces `Views/Styles.cs`. S07 rec. 8, S01 note 2.
- Mirror: `PA:WinoStyle.cs`, `PA:WinoThemeSurfaces.cs`, `PA:WinoZoneView.cs`, `PA:Layout.cs`.
- Shared: `WinoTokens`, `WinoRgba`, `WinoThemeSurface` (`Wino.Presentation.Core`).
- Files: new `PG:WinoStyle.cs`, `PG:Resources/wino.css`, `PG:WinoZone.cs`; delete `H:Views/Styles.cs`; modify `H:LinuxApplication.cs`.
- Deps: F-01. Parallel: F-05.
- Shared touch: none.
- Verify: screenshots light and dark show unchanged or better PoC styling; GTK logs no CSS parser warning.

### F-03 · Wino icon font and the icon widget · M

- Scope: bundle the WinoIcons fonts (set per open question Q3; the file name is one constant) by linking from `icons/fonts` in the project file, register them per SP-03, `WinoIcons` helper and `WinoIcon` widget (glyph from `WinoIconGlyph`, size, mono or colourful by the icon-style preference, Light/Dark colour font by `Adw.StyleManager`, redraw on style change through a weak event), `WinoIcons.Button(...)` for icon-only buttons with an accessible label. Replace the Adwaita name switch in `ShellMenuRow` and the PoC mail row icons. S01 "Wino icon font", "Icon style"; S07 rec. 5.
- Mirror: `PA:WinoIcons.cs` (`WinoIcons`, `WinoIconView`), `controls/Wino.Presentation.Android/WinoIcons.cs`, `icons/README.md`.
- Shared: `WinoIconGlyph`, `WinoIconGlyphs`, `WinoControlRules`, `SpecialFolderGlyphs`, `MailProviderGlyphs`, `IconStyleContainer`.
- Files: new `PG:WinoIcons.cs`, `PG:WinoIcon.cs`, `PG:Native/FontRegistration.cs`; modify `PG:*.csproj` (font items copied to output), `H:Views/Shell/ShellMenuRow.cs`, `H:Views/Mail/MailRowCell.cs`.
- Deps: SP-03, F-01. Parallel: F-04.
- Shared touch: none (fonts are linked, not moved).
- Verify: sidebar shows Wino glyphs for Inbox, Drafts, Sent, Trash, Junk, Archive; glyph colour follows selection and dark mode; colourful style renders in colour; the AOT publish still runs and shows glyphs (one publish, counted as this package's second build).

### F-04 · List model adapter and cell factory · M

- Scope: `ObservableListModel<T>` (wraps `INotifyCollectionChanged`; Add, Remove, Replace, Move, Reset to one `Splice` per main-loop turn; item type per SP-01: GObject wrapper, or keys in a `StringList` + index map), `WinoCellFactory<TCell, TItem>` (setup, bind, unbind, teardown; per-cell `BindingScope`; removes handle-keyed entries on teardown), list helpers (load more near the end, scroll to key, restore scroll by key after a reset, selection bitset iteration, section headers per SP-02, hooks for drag source and context menu per cell). S07 rec. 2, 11.
- Mirror: `H:Views/Mail/MailListView.cs` (the PoC patterns to generalise), `CA:WinoCollectionView.cs`.
- Shared: `MailListProjection`, `MailListCollection`.
- Files: new `PG:Lists/ObservableListModel.cs`, `PG:Lists/WinoCellFactory.cs`, `PG:Lists/ListViewHelpers.cs`, `PG:Lists/ManagedItem.cs` (only if SP-01 passed).
- Deps: SP-01, SP-02, F-01. Parallel: F-03.
- Shared touch: T2 only if SP-01 passed (`GirCore.GObject-2.0.Integration` package version).
- Verify: a Debug-only test page lists 50,000 synthetic rows with sections, appends without a scroll jump, and frees cells (live-object count); the PoC mail list is not yet moved (ML-02 does it).

### F-05 · Page registry, navigation hosts and registration partials · M

- Scope: `LinuxPageRegistry` (`WinoPage` -> view factory + host) in per-area partials; hosts Window, ShellContent, RenderingFrame, Settings, SecondaryWindow; rewrite `GtkNavigationService.TryGetRoute`, `SetShellContentAsync`, `ShowInRenderingFrameAsync` on it and give every empty `INavigationHost` member a real body or an explicit "host not built yet" refusal that later packages replace; `LinuxViewModelRegistration` partials; move the three PoC routes onto the registry. S07 rec. 4.
- Mirror: `M:Infrastructure/MacPageRegistry*.cs`, `M:Infrastructure/MacViewModelRegistration*.cs`, `M:Infrastructure/AppKitNavigationService.cs`.
- Shared: `NavigationCoordinator`, `INavigationHost`, `INavigationService`, `RegisterWinoViewModels`.
- Files: new `H:Infrastructure/LinuxPageRegistry.cs`, `LinuxPageRegistry.Mail.cs`, `LinuxViewModelRegistration.cs`, `LinuxViewModelRegistration.Mail.cs`; modify `H:Infrastructure/GtkNavigationService.cs`, `H:LinuxComposition.cs`, `H:Views/Mail/MailListView.cs`, `H:Views/Shell/ShellView.cs` (lifecycle onto `WinoView`).
- Deps: F-01. Parallel: F-02.
- Shared touch: none.
- Verify: folders, list, reader and idle page behave as before; navigating to an unregistered page logs one refusal and does not crash; back to another folder releases the previous list view (live objects).

### F-06 · Actions, menus and context-menu helper · S

- Scope: `WinoMenus`: `Gio.Menu` builder with sections, submenus, stateful check and radio items and accelerator hints; a per-popup `Gio.SimpleActionGroup` whose handlers are released when the popover closes; `ContextMenu.Attach(widget, builder)` for right click (button 3), long press and the Menu key; conversion from the shared `ContextFlyoutMenuEntry` list. S07 rec. 6.
- Mirror: `PA:WinoMenus.cs`, `W:MenuFlyouts/WinoContextFlyout*.cs`, `W:MenuFlyouts/ContextFlyoutShortcutResolver.cs`.
- Shared: `ContextFlyoutMenuEntry`, `ContextFlyoutShortcutPolicy` (`Wino.Mail.Controls.Core`).
- Files: new `PG:WinoMenus.cs`, `PG:ContextMenu.cs`.
- Deps: F-01. Parallel: F-07.
- Shared touch: none.
- Verify: a temporary context menu on the reader subject (removed again by RD-02) opens by right click and by the Menu key; AT-SPI lists its items; no live action group after close.

### F-07 · Dialog framework and the basic dialogs · M

- Scope: generalise `AskAsync` into `AlertPresenter` (`Adw.AlertDialog`, N responses with appearance, markup off, optional extra child) and `FormDialog` (`Adw.Dialog` + `Adw.ToolbarView`, title, primary and secondary buttons, validation, `TaskCompletionSource`), owner window resolution per calling window, `Gtk.FileDialog` wrappers. Real implementations for: `ShowMessageAsync`, `ShowConfirmationDialogAsync`, `ShowThreeButtonDialogAsync`, `ShowHardDeleteConfirmationAsync`, `ShowWinoCustomMessageDialogAsync`, `ShowNotSupportedMessage`, `ShowReadOnlyCalendarMessage`, `ShowRecurringEventDeleteDialogAsync`, `ShowTextInputDialogAsync`, `ShowDialogAsync`, `PickFilesAsync`, `PickFilesMetadataAsync`, `PickFilePathAsync`, `PickWindowsFolderAsync`, `PickWindowsFileContentAsync` (15 of the 38). S01 "Dialogs framework", S02 1.6, S06 section 3, S07 rec. 7.
- Mirror: `M:Infrastructure/AppKitDialogService.cs`, `M:Views/Dialogs/FormSheet.cs`, `W:Services/DialogService.cs`, `W:Services/DialogServiceBase.cs`, `W:Dialogs/TextInputDialog.xaml`, `WinoMessageDialog`, `RecurringEventDeleteDialog`.
- Shared: `IDialogServiceBase`, `IMailDialogService`, `RecurringEventDeletion`.
- Files: new `H:Views/Dialogs/AlertPresenter.cs`, `H:Views/Dialogs/FormDialog.cs`, `H:Views/Dialogs/FilePickers.cs`, `H:Infrastructure/GtkDialogService.Basic.cs`, `GtkDialogService.Pickers.cs`; modify `GtkDialogService.cs`, `GtkDialogService.Dismissed.cs`.
- Deps: F-01. Parallel: F-06.
- Shared touch: none expected (strings exist for the Windows dialogs).
- Verify: Debug actions open each dialog kind; AT-SPI presses each response and the awaited result is logged; a subject containing `&` and `<` shows literally; the file dialog opens (portal) and returns a path chosen by hand once (recorded as a manual step, AT-SPI cannot drive the portal dialog).

### F-08 · Theme service · M

- Scope: real `INewThemeService`: `RootTheme` applied through `Adw.StyleManager.ColorScheme` and persisted, `ElementThemeChanged` from `notify::dark`, accent (system accent through `Adw.StyleManager.AccentColor`, user accent through a generated `CssProvider` that redefines the accent named colours), `GetAvailableAccountColors`, icon style apply, the predefined Wino themes from `WinoThemeCatalog` (accent + forced element theme + gradient or wallpaper), `GetAvailableBackdropTypes` returns empty, a `ThemeBackdrop` widget the shell puts behind its content, floating-zones flag for F-02. Custom themes stay throwing until ST-08. S01 "Theme", "Accent colours", "App themes", "Icon style"; S07 rec. 9.
- Mirror: `M:Infrastructure/MacWinoThemeService.cs`, `M:Views/Shell/ThemeBackdropView.cs`, `M:Infrastructure/MacThemeImaging.cs`, `W:Services/NewThemeService.cs`.
- Shared: `INewThemeService`, `WinoThemeCatalog`, `WinoThemeDefinition`, `IPreferencesService`, `IUnderlyingThemeService`, `CustomThemeColorCatalog`.
- Files: rewrite `H:Infrastructure/GtkThemeService.cs`; new `PG:ThemeBackdrop.cs`, `PG:ThemeCss.cs`; modify `H:Wino.Mail.Linux.csproj` (link wallpapers from `W:BackgroundImages`).
- Deps: F-02. Parallel: F-09, F-10.
- Shared touch: none (wallpapers linked).
- Verify: Debug action sets each of light, dark, system; accent change recolours the selected row and buttons live; a wallpaper theme and a gradient theme paint behind the panes and switch the zones to floating; restart keeps the choice.

### F-09 · Info bar host · S

- Scope: `InfoBarHost`: queue, 5 s timeout, pause on hover, severity (Informational and Success as `Adw.Toast`, Warning and Error as a banner strip with a Wino glyph), optional action button, both `InfoBarMessage` overloads, `InfoBarMessageRequested`; `WinoInfoBar` control for inline page banners. S01 "Info bars".
- Mirror: `M:Infrastructure/InlineInfoBarPresenter.cs`, `CA:Common/WinoInfoBar*.cs`, `W:ShellWindow.xaml.cs` (`ShowInfoBarMessage`).
- Shared: `InfoBarMessageRequested`, `InfoBarMessageType`.
- Files: new `CG:Common/WinoInfoBar.cs`, `H:Infrastructure/InfoBarHost.cs`; modify `H:Views/MainWindow.cs`, `H:Infrastructure/GtkDialogService.cs`.
- Deps: F-02, F-03. Parallel: F-08, F-10.
- Shared touch: none.
- Verify: Debug action raises one message per severity and one with an action; AT-SPI reads the text and presses the action; three queued messages show in order.

### F-10 · Contact picture, account icon and the sender picture provider · M

- Scope: `WinoContactPicture` (sizes 40, 32, 26; initials with the shared palette index; photo; provider badge), `WinoAccountIcon` (profile picture or provider logo, merged-account stack), `LinuxSenderPictureProvider` (texture cache by address, decode off the main thread, `PictureChanged`, honours `IsShowSenderPicturesEnabled`, Gravatar and favicon through the shared thumbnail service), `Gdk.Texture` helpers from bytes and files. S02 "Avatars and sender pictures", S05 "Contact avatar", S07 rec. 10.
- Mirror: `CA:Common/WinoContactPicture.cs`, `M:Infrastructure/MacSenderPictureProvider.cs`, `W:` `controls/Wino.Mail.Controls.WinUI/ContactPicture/*`.
- Shared: `IThumbnailService`, `ThumbnailAdded`, `ContactPictureIdentity`, `ContactPictureAppearance`, `MailAccountIconInfoFactory`, `AccountIconInfo`.
- Files: new `CG:Common/WinoContactPicture.cs`, `CG:Common/WinoAccountIcon.cs`, `PG:Textures.cs`, `H:Infrastructure/LinuxSenderPictureProvider.cs`; modify `H:Views/Mail/MailReaderView.cs`.
- Deps: F-02, F-03. Parallel: F-08, F-09.
- Shared touch: none.
- Verify: the reader shows initials with the same colour as Windows for three lab senders; a sender with a stored thumbnail shows the photo; turning sender pictures off in preferences (by hand in the preferences file until ST-03) falls back to initials.

### Phase 2: shell

### SH-01 · Window and pane layout · M

- Scope: minimum size, default 1360x860, size and maximised state persisted (no position on Wayland); outer `Adw.OverlaySplitView` with a drag handle (220 to 380), remembered width (`OpenPaneLength`), remembered open state (`IsNavigationPaneOpened`), collapse to overlay below 1008 px; mode host (one child per mode, built lazily); list and reader split with `MailListPaneLength` persisted (debounced), stacked with push navigation below 640 px; theme backdrop layer under everything. S01 "Main window structure", "Pane collapse", "Window sizing", "Adaptive layout", "Pane splitters".
- Mirror: `M:Views/WinoAppShellViewController.cs`, `M:WinoShellWindow.cs`, `W:Views/WinoAppShell.xaml(.cs)`, `W:ShellWindow.xaml.cs`.
- Shared: `WinoAppShellViewModel`, `IStatePersistanceService`, `IPreferencesService`, `IConfigurationService`.
- Files: rewrite `H:Views/MainWindow.cs`, `H:Views/Shell/ShellView.cs`; new `H:Views/Shell/ShellLayout.cs`, `H:Views/Shell/PaneResizeHandle.cs`, `H:Infrastructure/WindowStateStore.cs`.
- Deps: F-05, F-08. Parallel: SH-04 (only after the `ShellView` split into `ShellLayout` + sidebar lands; otherwise sequential), F-09, F-10.
- Shared touch: none.
- Verify: resize handle changes the sidebar width and a restart restores it; `debug-resize` to 1008 and 640 shows overlay sidebar and stacked list and reader with a back button; window size restored after restart.

### SH-02 · Mode switcher and mode activation · M

- Scope: `WinoModeSwitcher` (card with five icon segments, selected segment filled glyph in accent), at the sidebar foot; activation of Mail, Calendar, Contacts, To Do through `ShellModeActivator` (remove the Mail-only return), Settings segment opens the Settings window (ST-02; a placeholder window until then); Ctrl+1..4; per-mode shell menu from `IShellMenuProviderResolver`; an `Adw.StatusPage` placeholder for modes whose page is not registered yet; honours the app-modes visibility preference. S01 "App mode switching", S04 and S05 "Mode switcher entry".
- Mirror: `CA:Shell/WinoModeSwitcher.cs`, `W:` `controls/Wino.Mail.Controls.WinUI/ModeSwitcher/*`, `M:Views/WinoAppShellViewController.cs`.
- Shared: `ShellModeActivator`, `ShellModeSelection`, `WinoApplicationMode`, `WinoControlRules` mode segments, `AppModeVisibility`, `IApplicationShellHost.EnsureShellAsync`.
- Files: new `CG:Shell/WinoModeSwitcher.cs`; modify `H:Views/Shell/ShellView.cs`, `H:Infrastructure/GtkApplicationHosts.cs`.
- Deps: SH-01, F-03. Parallel: SH-03.
- Shared touch: none.
- Verify: AT-SPI clicks each segment; the sidebar shows the mode's menu (calendar list, contact books, task lists come from the shared ViewModels even before their pages exist); Ctrl+1..4 through `debug-shortcut` once SH-06 lands.

### SH-03 · Header bar · M

- Scope: content header: sidebar toggle, sync button with idle, syncing and failed states and tooltip, title and subtitle, centred search entry host (the search target contract of the current page, a GTK twin of the macOS head-local `IShellSearchTarget`), trailing slots for Daily briefing, What's New and the Wino Account button (filled by IN-02, WN-01, AC-12), primary menu (Preferences, Keyboard shortcuts, What's New, About, Quit) with `app.*` actions, overflow below 560 px; back button, mouse back button and Alt+Left with `IConfirmBackNavigation`. S01 "Title bar", "Main menu", "Sync status indicators", "Back navigation".
- Mirror: `M:Views/Shell/ShellToolbar.cs`, `M:Infrastructure/ShellContracts.cs`, `W:ShellWindow.xaml(.cs)` (`TitleBar`, `ApplyTitleBarWidthState`, `RefreshShellSynchronizationButton`), `CA:Shell/WinoSynchronizationButton*`.
- Shared: `IShellMenuProvider` sync members, `ShellSynchronizationState`, `AccountSynchronizationProgressUpdatedMessage`, `IBackNavigationAware`, `IConfirmBackNavigation`.
- Files: new `H:Views/Shell/ShellHeaderBar.cs`, `H:Infrastructure/ShellContracts.cs`, `CG:Shell/WinoSynchronizationButton.cs`, `H:Infrastructure/AppActions.cs`.
- Deps: SH-01, F-03, F-06. Parallel: SH-02.
- Shared touch: T1 if a primary-menu label has no existing key.
- Verify: sync button starts a sync on the lab account and shows the syncing state; title follows the selected folder; the primary menu lists five items; at 560 px the trailing buttons move into the overflow menu.

### SH-04 · Sidebar rows · M

- Scope: keyed, incremental sidebar (update only the rows whose item raised, keep focus and scroll), `WinoShellRow` control: New item row, account row with `WinoAccountIcon`, folder rows with special-folder glyphs and custom icon and colour, section headers, "More", merged and linked accounts with the avatar stack and `AccountGroupExpansionStore`, unread pill honouring the badge-location setting, account sync spinner and determinate progress, error state and the "fix account issues" attention row, selection (subtle fill with accent pipe when a theme is active), Left and Right to collapse and expand, Enter to invoke, type-ahead. The local copy of the "grouping parents expand in place" rule stays in the head. S02 1.1 rows 2 to 7 and 10, S01 "Navigation pane".
- Mirror: `M:Views/Shell/ShellSidebarViewController.cs`, `ShellPaneCells.cs`, `ShellPaneRows.cs`, `CA:Shell/WinoShellRowView.cs`, `CA:Shell/WinoUnreadPill.cs`, `W:Styles/ShellMenu/ShellMenuTemplates.xaml`.
- Shared: `ShellMenuRowPresentation.Flatten/Describe`, `ShellMenuItemRules`, `ShellMenuRowContent`, `MenuItems/*`, `FolderCustomizationItemViewModel`, `RefreshUnreadCountsMessage`.
- Files: new `CG:Shell/WinoShellRow.cs`, `CG:Shell/WinoUnreadPill.cs`, `H:Views/Shell/ShellSidebarView.cs`; delete `H:Views/Shell/ShellMenuRow.cs`; modify `H:Views/Shell/ShellView.cs`.
- Deps: F-03, F-04, F-10, SH-01. Parallel: SH-03.
- Shared touch: none.
- Verify: with two leased lab accounts: both account sections, folder glyphs, unread counts that change after `deliver` on the lab control API without the list losing scroll or focus; expanding "More" keeps the selection; a merged inbox (after AC-01) shows the stack.

### SH-05 · Sidebar context menus and folder dialogs · M

- Scope: folder menu (rename, create subfolder, delete, mark all read, empty, move, pin to favourites, sync on or off, and the rest of `GetAvailableFolderActions` with enabled state), account menu (settings, fix account, synchronise, create root folder, mark inboxes read), merged-account menu; rename and create-folder dialogs (`ShowRenameFolderDialogAsync` with the rename-on-server option). S02 1.1 "Folder context menu", "Account context menu"; 1.6 rename.
- Mirror: `M:Views/Shell/ShellSidebarContextMenus.cs`, `M:Views/Shell/FolderOperationPresentation.cs`, `M:Infrastructure/AppKitDialogService.Folders.cs`, `W:MenuFlyouts/FolderOperationFlyout.cs`, `W:Dialogs/RenameFolderDialog.xaml`.
- Shared: `IMailShellClient.PerformFolderOperationAsync`, `FolderOperation`, `FolderOperationDisplay`, `FolderOperationMenuItem`, `MarkAccountInboxesAsReadAsync`, `CreateRootFolderAsync`, `FolderRenameDialogResult`.
- Files: new `H:Views/Shell/ShellSidebarContextMenus.cs`, `H:Infrastructure/GtkDialogService.Folders.cs`; modify `H:Views/Shell/ShellSidebarView.cs`, `GtkDialogService.Dismissed.cs`.
- Deps: SH-04, F-06, F-07. Parallel: SH-06, ML-01.
- Shared touch: none.
- Verify: on the lab IMAP account: create a folder, rename it, mark all read, delete it; each result visible on the server through a second IMAP read (the lab control API or a re-sync).

### SH-06 · Keyboard shortcuts routing · M

- Scope: window-level routing of the enabled shortcut snapshot (rebuilt on `KeyboardShortcutsChanged`), mode awareness and `KeyboardShortcutContextPolicy` (no bare keys while a `Gtk.Editable` or the web view has focus), `IsShiftKeyPressed` from the last event state, `Adw.ShortcutsDialog` (Ctrl+?), `app.*` accelerators for the fixed ones (Ctrl+, Ctrl+Q, Ctrl+1..4, Ctrl+N), `KeyboardShortcuts: true`; `debug-shortcut` action. S01 "Keyboard shortcuts", S02 1.4 "Keyboard shortcuts in mail".
- Mirror: `M:AppDelegate.Shortcuts.cs`, `M:Infrastructure/ShortcutKeys.cs`, `W:Services/KeyboardShortcutController.cs`.
- Shared: `IKeyboardShortcutService`, `KeyboardShortcutAction`, `KeyboardShortcutContextPolicy`, `KeyboardShortcutTriggerDetails`, `IShortcutPlatformService`, page `KeyboardShortcutHook`s.
- Files: new `H:Infrastructure/ShortcutRouter.cs`, `H:Infrastructure/ShortcutKeys.cs`, `H:Views/Dialogs/ShortcutsOverview.cs`; modify `P:Services/LinuxPlatformServices.cs`, `P:LinuxPlatformRegistration.cs`, `H:Infrastructure/Debug/DebugActions.cs`.
- Deps: SH-03. Parallel: SH-05, SH-07.
- Shared touch: none.
- Verify: `debug-shortcut` for delete, mark read and reply (reply after ED-02) acts on the selected lab message; a bare-key shortcut does nothing while the search entry has focus; the shortcuts dialog lists the configured set.

### SH-07 · Secondary windows · S

- Scope: window manager for `INavigationHost.ShowInSecondaryWindowAsync`: an `Adw.Window` per hosted page registered with the application, title, size persistence per page kind, close handling with `IConfirmBackNavigation`, focus an existing window for the same key; `AdditionalWindows: true`; `debug-screenshot` and `debug-dump-tree` take a window selector. S01 "Multiple windows", S07 rec. 13.
- Mirror: `W:Services/WinoWindowManager.cs`, `W:HostedContentPopoutWindow.xaml(.cs)`, `M:Infrastructure/AppKitNavigationService.cs` (secondary window part).
- Shared: `INavigationHost`, `IPlatformCapabilities.AdditionalWindows`.
- Files: new `H:Infrastructure/WindowManager.cs`, `H:Views/SecondaryWindow.cs`; modify `H:Infrastructure/GtkNavigationService.cs`, `P:LinuxPlatformRegistration.cs`, `H:Infrastructure/Debug/DebugActions.cs`.
- Deps: F-05. Parallel: SH-06.
- Shared touch: none.
- Verify: a Debug action opens the idle page in a second window; it closes independently; the application quits only after the main window closes.

### SH-08 · Localisation pass for the head · S

- Scope: every hard-coded English string of the PoC (`MainWindow`, `LinuxApplication`, `GtkApplicationLifecycleHost`, `ShellView`, keyring error text) through `Translator`; initial language from `LC_MESSAGES`/`LANG` when the preference is unset; text direction set from the app language (`he_IL` mirrors). S01 "Localization", note 8.
- Mirror: `M:` use of `Translator` in `AppDelegate`.
- Shared: `ITranslationService`, `Translator`, `LanguageChanged`.
- Files: modify `H:LinuxApplication.cs`, `H:Views/MainWindow.cs`, `H:Infrastructure/GtkApplicationHosts.cs`; new `H:Infrastructure/LinuxLanguage.cs`.
- Deps: SH-01. Parallel: any.
- Shared touch: T1 (new English keys for the keyring and startup status texts).
- Verify: start with `LANG=de_DE.UTF-8` in an empty profile shows German folder names and menus; `LANG=he_IL.UTF-8` mirrors the layout (screenshot).

### Phase 3: mail list, folders and reader

### ML-01 · Mail row control · M

- Scope: `WinoMailRow` with the Wino row design: avatar (F-10), account colour bar and nickname by `AccountNicknamePosition`, sender, date right on the subject line, flag, pin, attachment and draft glyphs, busy spinner, unread accent bar and accent subject, preview on line 3 (lines per density), thread count and disclosure chevron, thread-child indent, group header strip; Compact, Medium and Spacious from `WinoMailRowMetrics` (56, 76, 88) re-applied on `PreferenceChangedMessage`; `MailListDensity: true`. `MailRowCell` becomes a thin binder. S02 1.2 "Item templates", "Preview text", "Avatars", "Flag/pin indicators", "Account colour", "Group headers".
- Mirror: `CA:MailList/WinoMailRowView.cs`, `WinoMailRowModel.cs`, `WinoMailTableRowView.cs`, `M:` `MailRowMapper`, `W:Views/Mail/MailListPage.xaml` templates, `W:Controls/ListView/WinoMailItemTemplateSelector.cs`.
- Shared: `MailRowPresentation`, `MailRowContent`, `WinoMailRowMetrics`, `MailItemDisplayMode`, `IPreferencesService`.
- Files: new `CG:MailList/WinoMailRow.cs`, `CG:MailList/WinoMailGroupHeader.cs`; rewrite `H:Views/Mail/MailRowCell.cs`; modify `P:LinuxPlatformRegistration.cs`.
- Deps: F-03, F-10. Parallel: SH-05, SH-06.
- Shared touch: none.
- Verify: screenshots of the three densities against the Windows row order (sender line, subject line, preview); unread, flagged, attachment and draft lab messages show their glyphs; selected row in light and dark.

### ML-02 · Mail list engine and performance · M

- Scope: move `MailListView` onto F-04; 120 ms throttle on projection changes; key-to-index map instead of the O(n) rebuild; middle-splice detection (port of macOS `TryApplyIncrementally`); loading-more footer; scroll restore by key after a reset; date groups as section headers per SP-02 (or header rows + sticky overlay); empty, folder-empty, no-search-result and no-online-result states; `CheckLoadMore` at 600 px. S02 "Performance headline", 1.2 "Virtualized list", "Group headers", "Empty states".
- Mirror: `M:Views/Mail/MailListPageViewController.List.cs` (`ProjectionThrottle`, `TryApplyIncrementally`, `CheckLoadMore`), `W:Controls/ListView/WinoListView.cs`.
- Shared: `MailListProjection`, `MailListCollection`, `MailListPageViewModel.LoadMoreItemsCommand`, `IsEmpty`, `IsFolderEmpty`, `HasNoOnlineSearchResult`.
- Files: rewrite `H:Views/Mail/MailListView.cs`; new `H:Views/Mail/MailListView.Projection.cs`, `H:Views/Mail/MailListStates.cs`.
- Deps: F-04, ML-01. Parallel: SH-06, SH-07, RD-01.
- Shared touch: none.
- Verify: a lab folder filled to 20,000 messages (control API) scrolls to the end with pages appended and no scroll jump; a delivered message appears at the top while the list is scrolled to the middle without moving the viewport; deleting a middle message removes one row; frame time during a sync burst logged by a Debug counter.

### ML-03 · Selection, select mode, threads and list keys · M

- Scope: `Gtk.MultiSelection` with Ctrl and Shift ranges, select mode with a leading check box per row, select-all with the tri-state box through the projection (never `SelectAll()` on the model), snapshot to the ViewModel, multi-selection summary pane in the reader area (count and bulk actions), thread expand and collapse (chevron, Left and Right on a head), Esc clears, Delete deletes, auto-select next. S02 1.2 "Selection", "Multi-selection summary", "Threading", "Keyboard in list". Row insert and remove animation is not available on `Gtk.ListView` (section 4).
- Mirror: `M:Views/Mail/MailListPageViewController.List.cs` (`MailTableView`, `SyncSelectionFromTable`, `ToggleThread`), `M:Views/Mail/ReadingPaneViews.cs` (`MailMultiSelectionView`), `W:Controls/ListView/ThreadedMailListView.cs`.
- Shared: `MailListSelectionSnapshotBuilder`, `ApplyMailSelectionSnapshot`, `IsMultiSelectionModeEnabled`, `SelectedItems`, `HasSingleFullySelectedThread`, `ExpandedThreadKeys`, `ThreadMailItemViewModel`.
- Files: new `H:Views/Mail/MailListView.Selection.cs`, `H:Views/Mail/MailMultiSelectionView.cs`; modify `MailListView.cs`, `MailRowCell.cs`.
- Deps: ML-02. Parallel: RD-01, RD-03.
- Shared touch: none.
- Verify: AT-SPI selects three rows through select mode; the summary pane shows 3; a lab thread expands and collapses and the head shows the count; select all on a 20,000-row folder returns in under 200 ms (logged).

### ML-04 · List header: pivots, filter and sort, notices, undo · M

- Scope: pivot bar (Focused and Other or pinned folders, with counts), filter and sort menu button (radio sections from `FilterOptions` and `SortingOptions`, label shows the active filter), select-mode toggle and select-all box placement, "Empty junk/trash" button, sync-disabled banner with Enable, "unread in Other" link, undo toast with the shared undo window. S02 1.1 "Folder pivots", "Other notice", "Sync disabled notice"; 1.2 "Sorting and grouping", "Filter bar", "Folder-specific buttons", "Undo bar".
- Mirror: `CA:MailList/WinoPivotBar.cs`, `M:Views/Mail/MailListPageViewController.Bars.cs`, `MailListPageViewController.List.cs` (`UpdatePivots`, `_filterMenu`), `W:MenuFlyouts/FilterMenuFlyout.cs`, `W:Views/Mail/MailListPage.xaml`.
- Shared: `PivotFolders`, `SelectedFolderPivot`, `FolderPivotViewModel`, `FilterOptions`, `SortingOptions`, `MailListProjectionOptions`, `IsUndoMailActionBarOpen`, `UndoMailAction`, `IsSynchronizationDisabledNoticeVisible`, `IsEmptyFolderButtonVisible`.
- Files: new `CG:MailList/WinoPivotBar.cs`, `H:Views/Mail/MailListHeader.cs`, `H:Views/Mail/MailListBars.cs`.
- Deps: ML-02, F-06, F-09. Parallel: ML-05, RD-02.
- Shared touch: none.
- Verify: filter Unread shows only unread lab messages; sort by name reorders; delete then Undo restores the message on the server; Empty on the lab Junk folder empties it after confirmation.

### ML-05 · Mail context menu · M

- Scope: right click, long press and Menu key on a row; targets are the selection when the clicked row is in it, else the row; items from `GetAvailableMailActions` with separators, Pin and Unpin, Categories submenu with check state (loaded before popup), thread expand and collapse, shortcut hints. S02 1.2 "Mail list context menu".
- Mirror: `M:Views/Mail/MailListPageViewController.Commands.cs` (`BuildContextMenu`), `W:MenuFlyouts/MailContextFlyoutBuilder.cs`.
- Shared: `GetAvailableMailActions`, `MailOperationDisplay`, `ToggleCategoryAssignmentAsync`, `GetAvailableCategoriesAsync`, `ChangePinnedStatusAsync`, `ExecuteMailOperationCommand`.
- Files: new `H:Views/Mail/MailListView.ContextMenu.cs`.
- Deps: ML-03, F-06. Parallel: ML-04, RD-02, RD-03.
- Shared touch: none.
- Verify: on lab messages: mark unread, flag, archive, delete, pin, assign a category; each state visible in the row and after a re-sync.

### ML-06 · Hover actions · M

- Scope: per-row overlay with the configured actions (position, size, animation from preferences), shown on pointer enter, hidden while dragging and in select mode, never for touch. S02 1.2 "Hover actions".
- Mirror: `CA:MailList/WinoMailRowView.cs` (hover logic), `W:` `controls/Wino.Mail.Controls.WinUI/HoverActions/*`.
- Shared: `HoverActionConfiguration`, `HoverActionKind`, `HoverActionPosition`, `HoverActionAnimation`, `IMailListActionResolver`.
- Files: new `CG:MailList/WinoHoverActions.cs`; modify `CG:MailList/WinoMailRow.cs`, `H:Views/Mail/MailRowCell.cs`.
- Deps: ML-01, ML-05. Parallel: ML-08, RD-04.
- Shared touch: none.
- Verify: `debug-screenshot` after a Debug action that forces the hover state on row 2; clicking the hover Delete through AT-SPI deletes that lab message; position setting changes the overlay alignment.

### ML-07 · Swipe actions · M

- Scope: touch and touchpad swipe on a row reveals the coloured action behind it and commits past the threshold, left and right actions from settings. S02 1.2 "Swipe actions". Low priority: scheduled in the polish phase.
- Mirror: `M:Views/Mail/MailListPageViewController.List.cs` (`SwipeActions`), `W:Controls/ListView/WinoSwipeControlItems.cs`.
- Shared: `ResolveSwipeOperation`, `ExecuteSwipeAsync`, swipe preferences.
- Files: new `CG:MailList/WinoSwipeRow.cs`; modify `CG:MailList/WinoMailRow.cs`.
- Deps: ML-06. Parallel: any non-mail-list package.
- Shared touch: none.
- Verify: a Debug action drives the swipe progress to 0.3 and 1.0 (screenshots); commit archives the lab message. Real touch is a manual owner check.

### ML-08 · Tile line: categories and intelligence tiles · S

- Scope: `WinoChip` (colour from hex through a cached generated CSS class), tile line with at most six chips and an ellipsis, pooled per cell; hidden in Compact. S02 1.2 "Categories and intelligence tiles line"; reader category chips reuse it.
- Mirror: `CA:Common/WinoChipView.cs`, `M:` `MailRowMapper.Tiles`, `W:` `controls/Wino.Mail.Controls.WinUI/IntelligenceTileBar/*`.
- Shared: `MailRowPresentation.HasTiles`, `IntelligenceTileModels`, `MailIntelligenceTileFactory`, category colours.
- Files: new `CG:Common/WinoChip.cs`, `CG:MailList/WinoTileLine.cs`; modify `CG:MailList/WinoMailRow.cs`.
- Deps: ML-01, SP-06. Parallel: ML-06, RD-01.
- Shared touch: none.
- Verify: a lab message with two categories shows two coloured chips in Medium and none in Compact.

### ML-09 · Move and folder pickers · M

- Scope: one folder-tree picker with type-to-filter, used by `ShowMoveMailFolderDialogAsync` (popover anchored to the Move button, dialog otherwise), `PickFolderAsync` and `HandleSystemFolderConfigurationDialogAsync` (five folder rows). S02 1.4 "Move to folder picker", S06 "Special folder configuration dialog".
- Mirror: `M:Views/Mail/MoveFolderPopover.cs`, `M:Infrastructure/AppKitDialogService.Folders.cs`, `W:Dialogs/MoveMailDialog.xaml`, `W:Dialogs/SystemFolderConfigurationDialog.xaml`.
- Shared: `IMailDialogService`, `IFolderService`, `FolderMenuItem`, `IMailItemFolder`.
- Files: new `H:Views/Dialogs/FolderPicker.cs`, `H:Views/Dialogs/SystemFolderConfigurationDialog.cs`; modify `H:Infrastructure/GtkDialogService.Folders.cs`, `GtkDialogService.Dismissed.cs`.
- Deps: F-07, SH-05 (shares `GtkDialogService.Folders.cs`). Parallel: ML-04, RD-03.
- Shared touch: none.
- Verify: move a lab message to a nested folder through the picker with a typed filter; the message is in that folder on the server.

### ML-10 · Drag mail onto folders · M

- Scope: drag source on list rows (the row or the whole selection), drag icon card with a count badge, `SetDragState`, drop targets on folder rows with highlight and move through the shared client, sidebar autoscroll; in-process payload per SP-07. S02 1.1 "Drag and drop of mail onto folders", 1.2 "Drag mail from list".
- Mirror: `M:Views/Mail/MailListPageViewController.Drag.cs`, `M:Views/Mail/MailDragPayload.cs`, `M:Views/Shell/ShellSidebarViewController.Drop.cs`, `W:Behaviors/MenuItemDropBehavior.cs`.
- Shared: `MailListPageViewModel.SetDragState`, `IMenuItemDropTarget`, `IMailShellClient` move.
- Files: new `H:Views/Mail/MailDragPayload.cs`, `H:Views/Mail/MailListView.Drag.cs`, `H:Views/Shell/ShellSidebarView.Drop.cs`.
- Deps: SP-07, ML-03, SH-04. Parallel: RD-04, RD-05.
- Shared touch: none.
- Verify: AT-SPI cannot drag on Wayland: a Debug action runs the drop path with the current selection onto a named folder (server state checked), and the pointer drag itself is one manual owner check with a screenshot of the highlight.

### ML-11 · Search bar, scope bar and online search · M

- Scope: header search entry bound to the page's search target (300 ms debounce, Enter submits, Esc clears, Ctrl+F focuses), history suggestions popover (Windows parity), scope bar above the list (Local or Online, current folder, subfolders, all; filter chips unread, attachments, flagged; active count; Done), online-search panel under local results. S02 1.3 rows 1, 2, 3 and 5.
- Mirror: `CA:MailList/WinoSearchScopeBar.cs`, `M:Views/Mail/MailListPageViewController.Search.cs`, `W:` `controls/Wino.Mail.Controls.WinUI/SearchBar/WinoSearchBar.cs`, `SearchBarHistoryEntry.cs`.
- Shared: `SearchAsync`, `ClearSearchAsync`, `SearchQuery`, `SearchScope`, `SearchScopes`, `MailSearchFilters`, `MailSearchFilterChip`, `SearchBarModels`, `IsOnlineSearchButtonVisible`.
- Files: new `CG:MailList/WinoSearchScopeBar.cs`, `CG:Shell/WinoSearchField.cs`, `H:Views/Mail/MailListView.Search.cs`.
- Deps: SH-03, ML-02. Parallel: ML-12 (after the scope bar file exists), RD-05, RD-06.
- Shared touch: none.
- Verify: searching a unique subject of a seeded lab message returns one row; the Unread chip filters; online search returns a message that is outside the synced range.

### ML-12 · Advanced search filter popover · S

- Scope: From, Subject, Keywords, date range with custom range, read status, attachments, flagged; Search and Cancel. S02 1.3 "Advanced filter popover".
- Mirror: `M:Views/Mail/MailListSearchFilterPopover.cs`, `W:Views/Mail/MailListPage.xaml` (`SearchFilterFlyout`).
- Shared: `MailSearchFilterEditor` (`ScopeOptions`, `DateRangeOptions`).
- Files: new `H:Views/Mail/MailSearchFilterPopover.cs`.
- Deps: ML-11, CG date picker from CA-02 if available, else `Gtk.Calendar`. Parallel: RD packages.
- Shared touch: none.
- Verify: From filter with a lab sender plus "has attachments" returns the seeded message only.

### RD-01 · Reader pane host and header · M

- Scope: reading pane stack (idle with keyboard hints, loading with the "downloading" text, message, multi-selection, popped-out placeholder), subject, sender row with `WinoContactPicture`, date, To, Cc and Bcc rows as chips with "n more" expand, contact card popover (copy address, new mail, add contact), category chips, unsubscribe button with confirmation, S/MIME status slot, web-process crash re-render. The reader stops being a singleton (`ShellView.Reader`): one instance per host, so RD-07 can open more. S02 1.5 rows 1 to 3, "Unsubscribe"; S03 "Sender header", "Web process crash recovery".
- Mirror: `M:Views/Mail/MailRenderingPageViewController.cs`, `MailRenderingPageViewController.Recipients.cs`, `M:Views/Mail/ReadingPaneViews.cs`, `M:Views/Mail/IdlePageViewController.cs`, `W:Views/Mail/MailRenderingPage.xaml`, `W:MenuFlyouts/ContactCardMenuFlyout.cs`.
- Shared: `MailRenderingPageViewModel` (`FromName`, `ToItems`, `CcItems`, `BccItems`, `CanUnsubscribe`, `UnsubscribeCommand`), `AccountContactViewModel`, `IdlePageViewModel`, `MailRowPresentation.FormatReaderDate`.
- Files: rewrite `H:Views/Mail/MailReaderView.cs`; new `H:Views/Mail/MailReaderHeader.cs`, `H:Views/Mail/MailRecipientRows.cs`, `H:Views/Mail/ContactCardPopover.cs`, `H:Views/Mail/ReadingPaneViews.cs`.
- Deps: F-10, F-09. Parallel: ML-02, ML-03, ML-08.
- Shared touch: none.
- Verify: a lab message with 12 recipients shows the expand control; the contact card copies the address (clipboard read back); a list-unsubscribe lab message shows the button.

### RD-02 · Reader command bar and mail actions · M

- Scope: command row from `MailReaderCommandLayout.Build` (primary and overflow by available width, order and visibility from the Reader actions setting): Reply, Reply all, Forward, Archive, Delete, Move, Flag, Mark read, plus More (reader view toggle, light or dark reader toggle, pop out, print, save as PDF, save as .eml, view source). Flag and read as toggles. S02 1.4 "Mail action toolbar", "Reader command bar", 1.5 "Reader theme toggle"; S03 "Reader-view toggle", "Reader command bar".
- Mirror: `M:Views/Mail/ReadingPaneViews.cs` (`MailReaderCommandBar`), `M:Views/Mail/MailListPageViewController.Commands.cs`, `W:Controls/OperationCommandBar.cs`, `W:Views/Mail/MailRenderingPage.xaml` (`RendererCommandBar`).
- Shared: `MailReaderCommandLayout`, `ReaderCommandLayoutEntry`, `MailOperationMenuItem`, `OperationClickedCommand`, `IsReaderViewEnabled`, `IsDarkWebviewRenderer`.
- Files: new `H:Views/Mail/MailReaderCommandBar.cs`; modify `H:Views/Mail/MailReaderView.cs`.
- Deps: RD-01, F-06, SP-06. Parallel: ML-04, ML-05.
- Shared touch: none.
- Verify: at 1360 px all primary actions show; at 640 px they move into More; Flag toggles the lab message and the list row; reader view toggle re-renders.

### RD-03 · Attachment strip · S

- Scope: horizontal strip of tiles (type icon through `Gio.ContentType`, name, size), click opens, menu Open, Save, Save all, fade edges on overflow. S02 1.5 "Attachments strip", S03 "Attachment strip".
- Mirror: `M:Views/Mail/MailRenderingPageViewController.cs` (`UpdateAttachments`, `AttachmentStripScrollView`), `M:Infrastructure/AttachmentFileIcons.cs`.
- Shared: `Attachments`, `OpenAttachmentCommand`, `SaveAttachmentCommand`, `SaveAllAttachmentsCommand`, `MailAttachmentViewModel`, `IAttachmentPlatformService`.
- Files: new `H:Views/Mail/AttachmentStrip.cs`, `H:Infrastructure/AttachmentFileIcons.cs`; modify `MailReaderView.cs`.
- Deps: RD-01, F-07. Parallel: ML-03, ML-05, ML-09.
- Shared touch: none.
- Verify: a lab message with a PDF and a PNG: both tiles, Save all into a scratch folder writes both files with the right sizes (path chosen by hand in the portal dialog).

### RD-04 · Reader session extras and privacy bars · M

- Scope: in the session: `SetDarkModeMethodAsync` override, pruned web context menu (Copy, Copy link, Copy image, Select all; link opening through `LinkOpener`), link-hover event surfaced; in the head: link target bar (bottom-start overlay with the Safe Links target), remote-content bars (withheld, junk, rule) with Load once and Always allow, privacy menu for sender and domain rules with tracker count. S02 1.5 "remote-content bars", "Link hover target bar"; S03 "Dark-mode method", "Blocked external images", "Link target status bar", "Context menu".
- Mirror: `M:Views/Mail/MailRenderingPageViewController.Privacy.cs`, `M:Views/Mail/ReaderLinkTargetBar.cs`, `controls/Wino.Editor.WebKit/WebKitHtmlMailReaderSession.cs`, `controls/Wino.Editor.WinUI/WinoMailRenderer.xaml.cs`.
- Shared: `MailRenderingPageViewModel.Privacy.cs`, `RemoteContentMode`, `RemoteContentRules`, `HtmlMailDarkModeMethod`, `RendererLinkHoverEventArgs`, `IHtmlMailReaderSession`.
- Files: modify `EG:WebKitGtkBrowserSession.cs`, `EG:WebKitGtkHtmlMailReaderSession.cs`; new `H:Views/Mail/ReaderLinkTargetBar.cs`, `H:Views/Mail/MailReaderView.Privacy.cs`.
- Deps: RD-01. Parallel: ML-06, ML-10.
- Shared touch: none.
- Verify: a lab HTML message with a remote image: blocked with the bar, Load once shows it, "Always allow sender" persists across messages; the web context menu has no Back, Reload or Open in new window (AT-SPI menu dump); hovering a link (Debug action that calls the hover handler) shows the URL.

### RD-05 · Print, PDF, .eml and message source · M

- Scope: `LinuxMailPrintPresenter` (`WebKit.PrintOperation`: dialog for print, print settings with output URI for PDF export), `Printing: true`, `PdfExport: true`; Save as .eml through the path picker; message source dialog (monospace, read only, Copy). S02 1.5 "Print and Save as PDF", "Save as .eml, view message source"; S03 "Printing".
- Mirror: `M:Infrastructure/MacMailPrintPresenter.cs`, `M:Infrastructure/AppKitDialogService.MessageSource.cs`, `W:Services/WindowsMailPrintPresenter.cs`, `W:Dialogs/MessageSourceDialog.xaml`.
- Shared: `IMailPrintPresenter`, `MailPrintRequest`, `PrintingResult`, `SaveAsPdfAsync`, `SaveAsEmlAsync`, `ShowMessageSourceDialogAsync`.
- Files: new `H:Infrastructure/LinuxMailPrintPresenter.cs`, `H:Views/Dialogs/MessageSourceDialog.cs`, `H:Infrastructure/GtkDialogService.MessageSource.cs`; modify `P:LinuxPlatformRegistration.cs`, `GtkDialogService.Dismissed.cs`, `H:LinuxComposition.cs` (through its partial).
- Deps: RD-02, F-07. Parallel: ML-10, ML-11.
- Shared touch: none.
- Verify: PDF export of a lab HTML message produces a PDF whose text contains the subject (`pdftotext`); the .eml parses with the same Message-Id; the print dialog opens (manual, one screenshot by the owner or a Debug-only "print to file" run).

### RD-06 · Invitation card in the reader · M

- Scope: card above the body for calendar invitations: title, time, location with join link, organiser and attendees, Accept, Tentative, Decline, Add to calendar, status after the response. S02 1.5 "Invitation card", S04 "Invitations in mail reader".
- Mirror: `M:Views/Mail/MailRenderingPageViewController.Invitation.cs`, `W:Controls/MailInvitationCard.xaml(.cs)`.
- Shared: `MailInvitationCardViewModel`, `MailRenderingPageViewModel.Invitation.cs`, `CalendarJoinLinkResolver`.
- Files: new `H:Views/Mail/MailInvitationCard.cs`; modify `MailReaderView.cs`.
- Deps: RD-01. Parallel: ML-11, ML-12.
- Shared touch: none.
- Verify: an iTIP invitation delivered to the lab IMAP account (control API `deliver` with a `text/calendar` part) shows the card; Accept sends the reply and the lab CalDAV calendar holds the event.

### RD-07 · Pop-out reader and the .eml reader window · M

- Scope: pop out the current message into its own window (new view, new ViewModel, new web session), placeholder in the pane, dock back; read-only `.eml` window (one per path) through `OpenMailFileAsync`; `message/rfc822` in the desktop entry and `GApplication.Open` routing. S02 1.5 "Open .eml", "Pop-out reader window"; S03 "Pop-out reader window / EML reader window".
- Mirror: `M:Views/Mail/MailRenderingPageViewController.PopOut.cs`, `M:Views/Mail/EmlReaderWindow.cs`, `W:HostedContentPopoutCoordinator.cs`.
- Shared: `MimeMessageInformation`, `ActivationRouter`, `IApplicationShellHost.OpenMailFileAsync`, `IActivationFileImportService`.
- Files: new `H:Views/Mail/EmlReaderWindow.cs`, `H:Views/Mail/MailReaderView.PopOut.cs`; modify `H:Infrastructure/GtkApplicationHosts.cs`, `H:Resources/app.winomail.Mail.desktop`, `H:Infrastructure/LinuxActivation.cs`.
- Deps: SH-07, RD-02. Parallel: ED-01.
- Shared touch: T5 (desktop-entry script gains the MIME type).
- Verify: pop out a lab message, select another message in the main window, both render; `gio open file.eml` (saved by RD-05) opens the reader window in the running instance.

### Phase 4: composer and editor

### ED-01 · WebKitGTK editor session · M

- Scope: extend `WebKitGtkBrowserSession` (editor document load through `AppleMailDocumentBuilder.BuildEditorAsync`, a `Message` event for non-reader message types); `WebKitGtkHtmlMailEditorSession` with its own copy of the `EditorCommandKind` to `execCommand` mapping and the `EditorSelectionState` to `EditorState` mapping, content get and set, `contentChanged`, `selectionState`, `shortcut`, `applicationShortcut`, link labels, signature, images, tables, find and replace calls, typography, theme, remote content policy reload, close preparation and the `Restarted` event; `autoCorrect` messages ignored (section 4). S03 section 2 and "Editor session over WebKitGTK", "Remote content in drafts", "Close preparation".
- Mirror: `controls/Wino.Editor.WebKit/WebKitHtmlMailEditorSession.cs`, `WebKitBrowserSession.cs`, `controls/Wino.Editor.WinUI/EditorBridge.cs`.
- Shared: `Wino.Editor.Core` (`EditorSessions.cs`, `EditorCommanding.cs`, `EditorJson.cs`, `editor.js`, `editor-images.js`, `editor-tables.js`), `IHtmlMailEditorSession`.
- Files: new `EG:WebKitGtkHtmlMailEditorSession.cs`, `EG:EditorCommandScripts.cs`; modify `EG:WebKitGtkBrowserSession.cs`, `controls/AGENTS.md` (T3).
- Deps: SP-05. Parallel: RD-07, DL-01.
- Shared touch: T3 (one line in `controls/AGENTS.md`: the project now has an editor session).
- Verify: a Debug-only window hosts the session: set HTML; because AT-SPI cannot type into a web view, a Debug action runs `exec` for bold, list and link and reads `getBodyContent`; state events logged; AOT publish runs the same page.

### ED-02 · Compose page · M

- Scope: `ComposeView` in the reading pane (route `MailComposePage`): header row (Send as the only filled button, Discard, pop-out placeholder), From selector (accounts and aliases), Subject, importance menu, read receipt and reply-to options, editor host, `GetHTMLBodyFunction` and `RenderHtmlBodyAsyncFunc` wiring, autosave on `contentChanged` (3 s), draft status and sync-failed banner, discard with confirmation, content-loss guard, reply and forward open with the quoted body, Ctrl+Enter sends. S03 section 4 rows "Composer page", "From", "Subject", "Importance", "Read receipt", "Send", "Draft autosave", "Reply / forward quoting".
- Mirror: `M:Views/Mail/ComposePageViewController.cs` and its partials (`.Options.cs`), `W:Views/Mail/ComposePage.xaml(.cs)`.
- Shared: `ComposePageViewModel`, `ComposePageViewModel.Signatures.cs`, `MessageImportance`, `ScheduleAutosave`, `SendAsync`, `DiscardDraftAsync`, `OnDraftFailed`.
- Files: new `H:Views/Mail/Compose/ComposeView.cs`, `ComposeView.Fields.cs`, `ComposeView.Options.cs`, `H:Infrastructure/LinuxPageRegistry.Compose.cs`, `LinuxViewModelRegistration.Compose.cs`.
- Deps: ED-01, RD-02, F-07. Parallel: ED-03.
- Shared touch: none.
- Verify: with a plain `Gtk.Entry` for To (until ED-03): send a message between two leased lab accounts and read it in the second; reply quotes the original; a draft typed and left appears in Drafts on the server after the autosave delay; discard removes it.

### ED-03 · Recipient token box · M

- Scope: `WinoRecipientTokenBox`: wrapping chips with remove buttons and a trailing entry; commit on Enter, comma, semicolon, Tab and focus-out through `ComposeRecipientRules.TryParseTyped`; Backspace removes the last chip; paste of several addresses splits; invalid state; chip menu (copy, edit, move to Cc or Bcc, remove); chip drag between fields (SP-07); Cc and Bcc reveal. S03 "To / Cc / Bcc token boxes".
- Mirror: `M:Views/Mail/ComposePageViewController.Recipients.cs` (`NSTokenField` delegate), `W:Views/Mail/ComposePage.xaml` (`TokenizingTextBox`).
- Shared: `ComposeRecipientRules`, `TryAddRecipient`, `ContainsAddress`, `GetAddressInformationAsync`, `AccountContactViewModel`.
- Files: new `CG:Compose/WinoRecipientTokenBox.cs`, `CG:Compose/WinoRecipientChip.cs`; modify `H:Views/Mail/Compose/ComposeView.Fields.cs`.
- Deps: SP-06, SP-07, F-06, F-10. Parallel: ED-02 (control only; the wiring into `ComposeView.Fields.cs` is the last step), ED-05.
- Shared touch: none.
- Verify: AT-SPI `set_text_contents` + a Debug commit action adds three recipients; a pasted "a@x, b@y; c@z" becomes three chips; an invalid address is refused with the shared rule's message; send reaches To and Cc recipients on the lab.

### ED-04 · Recipient suggestions · M

- Scope: non-modal popover under the entry with avatar, name, address, highlighted match and a remove-suggestion button; Up, Down, Enter, Tab and Esc handled on the entry; 120 ms delay and minimum length from the shared rules; contact-list expansion. S03 "Recipient autocomplete from contacts".
- Mirror: `M:Views/Mail/Compose/ComposeRecipientSuggestionPopup.cs`, `W:Views/Mail/ComposePage.xaml.cs` (`ResolveRecipientSuggestionsAsync`).
- Shared: `IRecipientSuggestionService`, `IRecipientHistoryService`, `RecipientSuggestion`, `SuppressSuggestionAsync`, `IPictureStorageService`.
- Files: new `CG:Compose/WinoRecipientSuggestionPopup.cs`; modify `CG:Compose/WinoRecipientTokenBox.cs`.
- Deps: ED-03. Parallel: ED-05, ED-06.
- Shared touch: none.
- Verify: after one sent mail to a lab address, typing its first two letters lists it; Enter commits the chip; removing the suggestion hides it on the next query.

### ED-05 · Format toolbar · M

- Scope: in-content toolbar bound to `EditorState` and `EditorCommandCapabilities`: font family (shared list intersected with installed fonts), size, bold, italic, underline, strikethrough, text colour and highlight swatches, paragraph style, line height, bullet and numbered lists, indent and outdent, alignment, clear formatting, undo and redo; focus returns to the web view after every command; overflow when narrow. S03 "Format toolbar" rows, "Text color", "Paragraph style".
- Mirror: `M:Views/Mail/Compose/EditorFormatToolbar.cs`, `controls/Wino.Editor.WinUI/WinoMailEditor.xaml(.cs)`, `EditorSelectorBar.xaml`.
- Shared: `EditorCommandKind`, `EditorCommandCapabilities`, `EditorColorValue`, `EditorParagraphStyleOption`, `EditorFontFamilyOption`, `EditorState`.
- Files: new `H:Views/Mail/Compose/EditorFormatToolbar.cs`, `H:Views/Mail/Compose/EditorColorMenu.cs`.
- Deps: ED-01. Parallel: ED-03, ED-04.
- Shared touch: none.
- Verify: Debug action selects all, AT-SPI presses Bold and a colour; `getBodyContent` contains the markup; the Bold toggle reflects the selection state; sent lab mail keeps the formatting in the second account's reader.

### ED-06 · Insert dialogs: link, image, table, emoji · M

- Scope: link dialog (URL, text, open in new window; Ctrl+K through the `shortcut` message; bubble labels), image insert (picker with image filter, data URI) and image properties dialog (alt, link, size), table dialog (rows and columns 1 to 20) and the row, column and delete commands of Windows, emoji through `Gtk.EmojiChooser`. S03 "Links", "Inline images", "Tables", "Emoji".
- Mirror: `M:Views/Mail/Compose/EditorDialogs.cs` (`LinkAsync`, `ImagePropertiesAsync`, `PickImagesAsync`, `TableAsync`), `W:Dialogs/EditorLinkDialog`, `EditorImagePropertiesDialog`, `EditorTableDialog`.
- Shared: `EditorLinkCommandArgs`, `EditorLinkRules`, `EditorImageInfo`, `EditorImagePropertiesCommandArgs`, `EditorTableCommandArgs`.
- Files: new `H:Views/Mail/Compose/EditorDialogs.cs`; modify `EditorFormatToolbar.cs`.
- Deps: ED-05, F-07. Parallel: ED-04, ED-07.
- Shared touch: none.
- Verify: insert a link, a 3x2 table and an image, send to the second lab account; the reader shows all three and the image arrives as a related MIME part.

### ED-07 · Composer attachments and drop zones · M

- Scope: attach through the file dialog, attachment tray with chips (name, size, remove, open), remove all, size inspection; page-level drop with two zones (Attach, Insert inline) drawn as an overlay, capture-phase target so the web view does not navigate; paste image from the clipboard (JS path, with the texture fallback of SP-05). S03 "Attachments add", "Attachment / inline-image drag and drop", "Paste image".
- Mirror: `M:Views/Mail/ComposePageViewController.Attachments.cs`, `ComposePageViewController.Drop.cs`, `controls/Wino.Editor.WinUI/WinoMailEditor.xaml.cs` (`EditorWebView_Drop`).
- Shared: `AttachFilesAsync`, `AddAttachment(SharedFile)`, `IncludedAttachments`, `RemoveAttachment`, `BeginAttachmentInspection`, `SharedFile`.
- Files: new `H:Views/Mail/Compose/ComposeAttachmentTray.cs`, `H:Views/Mail/Compose/ComposeDropOverlay.cs`; modify `ComposeView.cs`.
- Deps: ED-02, SP-05, SP-07. Parallel: ED-06, ED-08.
- Shared touch: T1 if the two drop-zone labels have no key (macOS has them; check first).
- Verify: attach two files (path through a Debug action that bypasses the portal picker), send, the second lab account receives both with equal hashes; the drop path through a Debug action for both zones; real drag is one manual check.

### ED-08 · Find and replace, spell check, editor theme and shortcuts · M

- Scope: find bar with replace row (Ctrl+F, Ctrl+H, next, previous, replace, replace all) over the JS finder; spell check on the process-wide `WebKit.WebContext` (enable, languages from the preference and locale; guesses in the web context menu) with the Enchant dependency documented; editor light or dark toggle following the app by default; `SetApplicationShortcuts` so configured shortcuts work while the web view has focus; composer web context menu (Cut, Copy, Paste, Paste as plain text, spelling guesses). S03 "Find / replace", "Spell check", "Composer theme toggle", "Keyboard shortcuts in editor", "Paste".
- Mirror: `M:Views/Mail/ComposePageViewController.Find.cs`, `M:Views/Mail/Compose/ComposeFindBar.cs`, `ComposePageViewController.Extras.cs` (`BuildSpellMenu`).
- Shared: `EditorFindDirection`, `EditorApplicationShortcutGesture`, `IsComposerSpellCheckEnabled` and language preferences, `KeyboardShortcutHook`.
- Files: new `H:Views/Mail/Compose/ComposeFindBar.cs`, `H:Views/Mail/Compose/ComposeView.Find.cs`, `EG:WebKitGtkSpellCheck.cs`; modify `EG:WebKitGtkBrowserSession.cs`.
- Deps: ED-02. Parallel: ED-07, ED-09.
- Shared touch: T6 (Linux guide: `libenchant-2-2` and `hunspell-*` packages).
- Verify: replace all changes three occurrences (body read back); with `hunspell-en-us` installed a misspelt word has guesses in the context menu (AT-SPI menu dump); the editor follows `debug-theme`.

### ED-09 · Signatures and templates in the composer · S

- Scope: signature menu (list, none, manage) applied through `SetSignatureAsync`; templates popover with search, apply, Manage and Create links. S03 "Signature", "Email templates / quick text".
- Mirror: `M:Views/Mail/ComposePageViewController.Extras.cs` (`RebuildSignatureMenu`), `M:Views/Mail/Compose/ComposeTemplatePopover.cs`.
- Shared: `ComposePageViewModel.Signatures.cs`, `ISignatureService`, `LoadEmailTemplatesAsync`, `IEmailTemplateService`.
- Files: new `H:Views/Mail/Compose/ComposeSignatureMenu.cs`, `H:Views/Mail/Compose/ComposeTemplatePopover.cs`.
- Deps: ED-02. Parallel: ED-08, ED-10.
- Shared touch: none.
- Verify: with a signature and a template seeded in the profile database by a scratchpad script over the real services (until ST-11 and ST-12 exist): switching signatures replaces the signature block only; applying a template fills subject and body.

### ED-10 · Pop-out composer · S

- Scope: "Open in new window" detaches the draft into a secondary window (fresh view and session, content carried through the ViewModel), dock back, closing the window keeps the draft, several composer windows at once. S03 "Pop-out composer / dock".
- Mirror: `M:Views/Mail/ComposePageViewController.cs` (`PopOutAsync`, `DockAsync`), `W:HostedContentPopoutCoordinator.cs`.
- Shared: `ComposeDetachedDraftRequested`, `ComposeDraftRequestedEventArgs`, `CloseRequested`.
- Files: new `H:Views/Mail/Compose/ComposeView.PopOut.cs`; modify `H:Infrastructure/WindowManager.cs`.
- Deps: ED-02, SH-07. Parallel: ED-09, ED-11.
- Shared touch: none.
- Verify: detach a half-written draft, the text is intact in the new window, send from it; a second composer opens in the pane meanwhile.

### ED-11 · Rich-text dialog editor and the signature editor dialog · M

- Scope: `WinoRichTextEditorView` (editor session + compact format toolbar) for dialogs and settings pages; `ShowSignatureEditorDialog`. Used by ST-11, ST-12 and CA-10. S03 "Rich-text dialog editor", S06 "Signature editor".
- Mirror: `M:Views/Dialogs/WinoRichTextEditorView.cs`, `M:Views/Dialogs/SignatureEditorSheet.cs`, `M:Infrastructure/AppKitDialogService.Signature.cs`, `W:Dialogs/SignatureEditorDialog.xaml`.
- Shared: `IHtmlMailEditorSession`, `ISignatureService`.
- Files: new `H:Views/Dialogs/WinoRichTextEditorView.cs`, `H:Views/Dialogs/SignatureEditorDialog.cs`, `H:Infrastructure/GtkDialogService.Signature.cs`; modify `GtkDialogService.Dismissed.cs`.
- Deps: ED-05. Parallel: ED-10, DL-01.
- Shared touch: none.
- Verify: Debug action opens the signature dialog, sets a name and formatted body, the returned signature HTML is logged and non-empty.

### Phase 5: remaining dialogs

Dialogs owned by another package: basic and pickers F-07; folders SH-05 and ML-09; message source RD-05; signature ED-11; keyboard shortcut recorder ST-09; account creation and IMAP validation AC-06; Wino Account dialogs AC-12; purchase channel AC-14; consent AC-15; app lock PL-01; editor link, image and table ED-06. After DL-02 `GtkDialogService.Dismissed.cs` holds only methods whose package has not landed.

### DL-01 · List pickers · M

- Scope: one `ListPickerDialog` (title, rows with icon, subtitle and optional colour dot, optional search, single choice) behind `PickAccountAsync`, `ShowAccountPickerDialogAsync`, `ShowSingleCalendarPickerDialogAsync`, `ShowTaskListPickerDialogAsync`, `ShowContactDestinationPickerDialogAsync`; `ShowNewContactListDialogAsync` (name + account). S05 "Dialogs", S06 section 3.
- Mirror: `M:Infrastructure/AppKitDialogService.AccountPicker.cs`, `.CalendarPicker.cs`, `.ContactsTasks.cs`, `W:Dialogs/AccountPickerDialog`, `SingleCalendarPickerDialog`, `TaskListPickerDialog`, `ContactDestinationPickerDialog`, `NewContactListDialog`.
- Shared: `IMailDialogService`, `AccountCalendarViewModel`, `ContactListCreationResult`.
- Files: new `H:Views/Dialogs/ListPickerDialog.cs`, `H:Infrastructure/GtkDialogService.ListPickers.cs`; modify `GtkDialogService.Dismissed.cs`.
- Deps: F-07, F-10. Parallel: ED-01, ED-11, DL-02.
- Shared touch: none.
- Verify: Debug actions open each picker with the leased accounts' data; AT-SPI chooses the second row; the logged result matches.

### DL-02 · Alias, category and the drag list · M

- Scope: `ShowCreateAccountAliasDialogAsync` (`ICreateAccountAliasDialog`), `ShowEditMailCategoryDialogAsync` (name + colour), `WinoDragList` control (reorder by drag and by keyboard Alt+Up and Alt+Down, used by account reorder, reader actions, folder customisation, filters), `ShowAccountReorderDialogAsync`. S06 section 3 "Account reorder", "Create alias", "Mail category edit".
- Mirror: `M:Infrastructure/AppKitDialogService.AccountReorder.cs`, `.AliasCategory.cs`, `M:Views/Settings/SettingsDragList.cs`, `W:Dialogs/AccountReorderDialog`, `CreateAccountAliasDialog`, `EditMailCategoryDialog`.
- Shared: `ICreateAccountAliasDialog`, `CategoryEditor`, `IAccountService`.
- Files: new `CG:Settings/WinoDragList.cs`, `H:Views/Dialogs/AccountReorderDialog.cs`, `H:Views/Dialogs/CreateAliasDialog.cs`, `H:Views/Dialogs/EditMailCategoryDialog.cs`, `H:Infrastructure/GtkDialogService.Accounts.cs`.
- Deps: F-07, SP-07. Parallel: DL-01, ST-01.
- Shared touch: none.
- Verify: reorder two leased accounts by keyboard through AT-SPI; the sidebar order follows and survives a restart.

### Phase 6: settings, accounts and sign-in

### ST-01 · Settings rows and the settings binder · M

- Scope: in `CG:`: `WinoSettingsCard` and `WinoSettingsExpander` equivalents on `Adw.ActionRow`, `SwitchRow`, `ComboRow`, `SpinRow`, `EntryRow`, `PasswordEntryRow`, `ExpanderRow`, `ButtonRow` with the Windows API surface (Header, Description, HeaderIcon as Wino glyph, Content, IsEnabled, Command), trailing controls, group headers; `SettingsBinder` (toggle, choice by stable id, number with the shared value rules, text, command, navigation row, visibility), validation state, disabled state. S06 fact 5, S07 rec. 15.
- Mirror: `CA:Settings/*` (`WinoSettingsCard`, `WinoSettingsExpander`, `SettingsBinder`), `W:` `SettingsCard` usage in `W:Views/Settings/*.xaml`.
- Shared: settings value rules in `Wino.Mail.Controls.Core`, `Wino.Presentation.Core.Bindings`.
- Files: new `CG:Settings/WinoSettingsRows.cs`, `CG:Settings/WinoSettingsGroup.cs`, `CG:Settings/SettingsBinder.cs`, `CG:Settings/WinoSettingsPage.cs`.
- Deps: F-01, F-03. Parallel: ST-02, DL-02, any mail package.
- Shared touch: none.
- Verify: a Debug gallery page shows every row kind in light and dark, enabled and disabled; AT-SPI toggles a switch and a bound test object changes.

### ST-02 · Settings window and catalog navigation · M

- Scope: the Settings window (`Adw.Window`, sidebar from `ISettingsCatalog` in the Windows group order with Wino glyphs and search over keywords, content `Adw.NavigationView` with back for sub-pages, breadcrumbs title), `LinuxPageRegistry.Settings*.cs` partial host, `IApplicationShellHost.ShowSettingsAsync` (also deep links to a page), pages hidden by `SettingsPageAvailability`, Ctrl+, and the mode-switcher segment, a placeholder for pages not built yet. S01 "Settings container", S06 facts 1 to 3.
- Mirror: `M:Views/Settings/SettingsWindowController.cs`, `SettingsSidebarViewController.cs`, `SettingsPageCatalog.cs`, `M:Infrastructure/MacPageRegistry.Settings.cs`, `W:Views/SettingsPage.xaml`, `SettingOptionsPage.xaml`.
- Shared: `SettingsCatalog`, `SettingsNavigationInfoProvider`, `ISettingsCatalog`, `SettingsPageAvailability`, `SettingsPageViewModel`, `SettingOptionsPageViewModel`, `SettingsMenuProvider`.
- Files: new `H:Views/Settings/SettingsWindow.cs`, `SettingsSidebarView.cs`, `SettingsPageHost.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.cs`, `LinuxViewModelRegistration.Settings.cs`; modify `H:Infrastructure/GtkApplicationHosts.cs`.
- Deps: F-05, SH-02, SH-07. Parallel: ST-01.
- Shared touch: none.
- Verify: the sidebar lists the same groups and order as Windows minus capability-hidden pages (compare with the catalog output of a scratchpad script); search "signature" filters to the right pages; back from a sub-page returns to its parent.

### ST-03 · Pages: General, Mail preferences, Message list · M

- Scope: App preferences (language with restart note, close behaviour, launch at sign-in row bound to the startup service, startup item; store-update row omitted), Mail preferences (sync interval with `ConfigurableSyncIntervals: true`, search mode, undo, mark-as-read mode and timer, auto-select next, delete protection, empty junk), Message list (hover actions position, size and animation; swipe actions; threads; date grouping; preview; sender pictures, favicons, Gravatar; spacing; time format; account nickname; mark read on trash; action scope; clear avatar cache). S06 2.4 "General", 2.5 "Mail preferences", "Message list".
- Mirror: `M:Views/Settings/AppPreferencesPageViewController.cs`, `MailPreferencesPageViewController.cs`, `MessageListPageViewController.cs`; `W:Views/Settings/AppPreferencesPage`, `MailPreferencesPage`, `MessageListPage`.
- Shared: `AppPreferencesPageViewModel`, `MailPreferencesPageViewModel`, `MessageListPageViewModel`, `ITranslationService`, `IStartupIntegrationService`.
- Files: new `H:Views/Settings/AppPreferencesPage.cs`, `MailPreferencesPage.cs`, `MessageListPage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.General.cs`; modify `P:LinuxPlatformRegistration.cs`.
- Deps: ST-01, ST-02. Parallel: ST-04, ST-05, ST-06 (each owns its own registry partial).
- Shared touch: none.
- Verify: changing density, preview lines and sender pictures changes the open mail list at once; language to German and restart; mark-as-read timer honoured on a lab message.

### ST-04 · Pages: Read and compose, Reader actions, Privacy and security, Remote content rules · M

- Scope: reader font, size and preview, load styles, plain-text links, dark mode method, message colours, composer font, spell check and language (auto-correct row omitted, section 4), reader actions entry; Reader actions page (drag list, preview, reset, apply and discard); Privacy and security (link browser per account from installed handlers of `x-scheme-handler/https`, confirm links, strip tracking parameters, show sender address, remote content, suggestions; attachment security check row omitted); Remote content rules (searchable list, policy per rule, delete). S06 2.5 rows 3 to 5.
- Mirror: `M:Views/Settings/ReadComposePanePageViewController.cs`, `ReaderActionsPageViewController.cs`, `PrivacySecurityPageViewController.cs`, `RemoteContentRulesPageViewController.cs`; the matching `W:Views/Settings/*Page`.
- Shared: `ReadComposePanePageViewModel`, `ReaderActionsPageViewModel`, `PrivacySecurityPageViewModel(.LinkBrowser)`, `RemoteContentRulesPageViewModel`, `IRemoteContentRuleStore`, `IReaderRuntimeService`.
- Files: new `H:Views/Settings/ReadComposePanePage.cs`, `ReaderActionsPage.cs`, `PrivacySecurityPage.cs`, `RemoteContentRulesPage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Mail.cs`.
- Deps: ST-01, ST-02, DL-02 (drag list). Parallel: ST-03, ST-05, ST-06.
- Shared touch: none.
- Verify: reader font size changes the open message; reordering reader actions changes the reader command bar order; a rule added in RD-04 appears and deleting it blocks images again.

### ST-05 · Pages: Notifications and Unread badges · M

- Scope: Notification settings (mail scope, content, the two action buttons, per-account overrides as expanders, quiet hours with weekday toggles, calendar default reminder and snooze, test buttons; the push-relay section shows the shared "unsupported" state); Unread badges global page and per-account page (count source, per-folder table; the taskbar-badge and tray rows hidden by capability). S06 2.4 "Notification settings", 2.1 "Unread badges".
- Mirror: `M:Views/Settings/NotificationSettingsPageViewController.cs`, `UnreadBadgeSettingsPageViewController.cs`, `AccountUnreadBadgePageViewController.cs`; `W:Views/Settings/NotificationSettingsPage`, `UnreadBadgeSettingsPage`, `AccountUnreadBadgePage`.
- Shared: the notification settings ViewModel, `UnreadBadgeSettingsPageViewModel`, `AccountUnreadBadgePageViewModel`, `INotificationBuilder` test methods.
- Files: new `H:Views/Settings/NotificationSettingsPage.cs`, `UnreadBadgeSettingsPage.cs`, `AccountUnreadBadgePage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Notifications.cs`.
- Deps: ST-01, ST-02. Parallel: ST-03, ST-04, ST-06.
- Shared touch: none.
- Verify: turning notifications off for one leased account suppresses its notification on `deliver` while the other still notifies (`gdbus monitor` on `org.freedesktop.Notifications`); excluding a folder from the badge changes the sidebar pill.

### ST-06 · Pages: Calendar, People and To Do preferences, Storage, About · M

- Scope: Calendar preferences, Calendar rendering, Calendar account settings, Contacts preferences, To Do preferences (sound row needs PL-06), Storage (scan, sizes, delete old mail, open folder), About (version, links, licence, diagnostics: enable logs, copy diagnostic id, upload logs, open log folder; store and review rows omitted). Seven small pages. S06 2.4 "About", 2.5 "Storage", 2.6.
- Mirror: `M:Views/Settings/CalendarPreferenceSettingsPageViewController.cs`, `CalendarRenderingSettingsPageViewController.cs`, `CalendarAccountSettingsPageViewController.cs`, `ContactsPreferenceSettingsPageViewController.cs`, `ToDoPreferenceSettingsPageViewController.cs`, `StoragePageViewController.cs`, `AboutPageViewController.cs`; the matching `W:Views/Settings/*Page`.
- Shared: `CalendarPreferenceSettingsPageViewModel`, `CalendarRenderingSettingsPageViewModel`, `CalendarAccountSettingsPageViewModel`, `ContactsPreferenceSettingsPageViewModel`, `ToDoPreferenceSettingsPageViewModel`, `StoragePageViewModel`, `IAppMetadataService`, `IWinoLogger`.
- Files: new `H:Views/Settings/Calendar*SettingsPage.cs` (3), `ContactsPreferenceSettingsPage.cs`, `ToDoPreferenceSettingsPage.cs`, `StoragePage.cs`, `AboutPage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Modes.cs`.
- Deps: ST-01, ST-02. Parallel: ST-03, ST-04, ST-05.
- Shared touch: none.
- Verify: Storage shows non-zero sizes for the leased account and "delete older than 1 month" reduces it; About shows the assembly version; the diagnostic id copies to the clipboard.

### ST-07 · Personalization and the theme gallery · M

- Scope: theme mode, accent (system and custom), icon style (monochrome or colourful), compact account menu, hide Wino Account button, app modes; backdrop row omitted (section 4); theme gallery with thumbnails for the predefined themes and the user's custom themes, apply and preview. S06 2.4 "Personalization", S01 "App themes".
- Mirror: `M:Views/Settings/PersonalizationPageViewController.cs`, `ApplicationThemeGalleryPageViewController.cs`, `ThemeThumbnailView`, `CA:Settings/WinoThemeTile*`; `W:Views/Settings/PersonalizationPage`, `ApplicationThemeGalleryPage`.
- Shared: `INewThemeService`, `AppearanceSnapshotService`, `WinoThemeCatalog`, `IPreferencesService`.
- Files: new `H:Views/Settings/PersonalizationPage.cs`, `ApplicationThemeGalleryPage.cs`, `CG:Settings/WinoThemeTile.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Themes.cs`.
- Deps: F-08, ST-01, ST-02. Parallel: ST-09, ST-10, AC packages.
- Shared touch: none.
- Verify: each predefined theme applies (three screenshots: default, a gradient theme, a wallpaper theme, light and dark); icon style switch recolours sidebar glyphs live.

### ST-08 · Custom themes and the theme editor · M

- Scope: `SaveCustomThemeAsync`, `GetCustomThemesAsync`, `DeleteCustomThemeAsync` and preview over `CustomThemeFileStore`; editor page (name, palette colours through colour buttons, wallpaper picker with focal point, surfaces, live preview, save validation). S01 "Custom themes", S06 "theme gallery/editor".
- Mirror: `M:Views/Settings/ApplicationThemeEditorPageViewController.cs`, `.Wallpaper.cs`, `ThemeColorWell.cs`, `ThemeSurfacePreviewView`; `W:Views/Settings/ApplicationThemeEditorPage`.
- Shared: `ApplicationThemeEditorPageViewModel`, `CustomThemePalette`, `CustomThemeSaveValidator`, `CustomThemeFileStore`.
- Files: new `H:Views/Settings/ApplicationThemeEditorPage.cs`, `CG:Settings/WinoThemePreview.cs`; modify `H:Infrastructure/GtkThemeService.cs`.
- Deps: ST-07. Parallel: any non-settings package.
- Shared touch: none.
- Verify: create a theme with a custom accent and a wallpaper, apply, restart, still applied; delete falls back to Default.

### ST-09 · Keyboard shortcuts page and the recorder dialog · M

- Scope: list (action, shortcut label, enabled switch), add, edit, delete, reset to defaults; recorder dialog (`ShowKeyboardShortcutDialogAsync`) capturing modifiers and key with GNOME key names and the conflict warning. S06 2.4 "Keyboard shortcuts", section 3 "Keyboard shortcut capture".
- Mirror: `M:Views/Settings/KeyboardShortcutsPageViewController.cs`, `HotKeyRecorderView.cs`; `W:Views/Settings/KeyboardShortcutsPage`, `W:Dialogs/KeyboardShortcutDialog`.
- Shared: `IKeyboardShortcutService`, `KeyboardShortcutDialogResult`, `IShortcutPlatformService`.
- Files: new `H:Views/Settings/KeyboardShortcutsPage.cs`, `H:Views/Dialogs/KeyboardShortcutDialog.cs`, `H:Infrastructure/GtkDialogService.Shortcuts.cs`.
- Deps: SH-06, ST-02. Parallel: ST-07, ST-10.
- Shared touch: none.
- Verify: disabling the Delete shortcut makes `debug-shortcut` delete do nothing; a recorded shortcut (Debug action feeds the key event to the recorder) acts on a lab message.

### ST-10 · Backup and restore page · S

- Scope: export and import of local data to a file (password prompt for an encrypted snapshot), the Wino Account section bound to the shared state; `FileBackup: true`. S06 2.4 "Backup and restore".
- Mirror: `M:Views/Settings/BackupRestorePageViewController.cs`, `W:Views/Settings/BackupRestorePage`.
- Shared: `BackupRestorePageViewModel`, `IWinoAccountDataSyncService`, `ISyncSnapshotKeyService`.
- Files: new `H:Views/Settings/BackupRestorePage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Backup.cs`; modify `P:LinuxPlatformRegistration.cs`.
- Deps: ST-02, F-07. Parallel: ST-07, ST-09.
- Shared touch: none.
- Verify: export from a profile with one leased account, import into an empty profile (second XDG folder set), the account and its preferences are there.

### ST-11 · Email templates pages · S

- Scope: list, create and edit (name, subject, rich body through ED-11), delete. S06 2.5 "Email templates".
- Mirror: `M:Views/Settings/EmailTemplatesPageViewController.cs`, `CreateEmailTemplatePageViewController.cs`.
- Shared: `EmailTemplatesPageViewModel`, `CreateEmailTemplatePageViewModel`, `IEmailTemplateService`.
- Files: new `H:Views/Settings/EmailTemplatesPage.cs`, `CreateEmailTemplatePage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Templates.cs`.
- Deps: ED-11, ST-02. Parallel: ST-12 (separate partials), AC packages.
- Shared touch: none.
- Verify: a created template is offered in the composer popover (ED-09) and fills subject and body.

### ST-12 · Signature management page · S

- Scope: per-account list, defaults for new and follow-up messages, add, edit (ED-11 dialog), delete. S06 2.1 "Signature management".
- Mirror: `M:Views/Settings/SignatureManagementPageViewController.cs`, `W:Views/Settings/SignatureManagementPage`.
- Shared: `SignatureManagementPageViewModel`, `ISignatureService`.
- Files: new `H:Views/Settings/SignatureManagementPage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Signature.cs`.
- Deps: ED-11, ST-02. Parallel: ST-11.
- Shared touch: none.
- Verify: default signature for new mail appears in a new composer; the follow-up default appears on reply.

### ST-13 · Mail filters list and editor · L

- Scope: list (provider-managed and Wino-managed, enable, reorder, duplicate, delete) and the editor (name, match all or any, dynamic condition rows with field, operator and value over about twelve field types, action rows, stop processing, validation). S06 2.1 "Mail filters". Plan as two steps (list, then editor) inside one package; split into ST-13a and ST-13b if the plan exceeds two builds.
- Mirror: `M:Views/Settings/MailFiltersPageViewController.cs`, `MailFilterEditorPageViewController.cs`, `.Rows.cs`, `MailFilterPresentation.cs`; `W:Views/Settings/MailFiltersPage`, `MailFilterEditorPage`.
- Shared: `MailFiltersPageViewModel`, `MailFilterEditorPageViewModel`, `IProviderMailFilterSynchronizer`.
- Files: new `H:Views/Settings/MailFiltersPage.cs`, `MailFilterEditorPage.cs`, `MailFilterEditorPage.Rows.cs`, `MailFilterPresentation.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Filters.cs`.
- Deps: ST-01, ST-02, DL-02, ML-09 (folder picker for move actions). Parallel: any non-settings package.
- Shared touch: none.
- Verify: a Wino-managed rule "subject contains X, move to folder F" on a leased account moves a delivered lab message; reorder and disable are honoured.

### ST-14 · Folder customization and mail categories pages · M

- Scope: folder customisation (pinned, categories, more; pin, hide, rename, reset, reorder), mail categories page (list, add, edit through DL-02, delete, favourite, refresh). S06 2.1 "Folder customization", "Mail categories".
- Mirror: `M:Views/Settings/FolderCustomizationPageViewController.cs`, `MailCategoryManagementPageViewController.cs`; `W:Views/Account/FolderCustomizationPage`, `W:Views/Settings/MailCategoryManagementPage`.
- Shared: `FolderCustomizationPageViewModel`, `MailCategoryManagementPageViewModel`, `CategoryEditor`.
- Files: new `H:Views/Settings/FolderCustomizationPage.cs`, `MailCategoryManagementPage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Category.cs`.
- Deps: ST-02, DL-02, SH-05. Parallel: AC-01, ST-07.
- Shared touch: none.
- Verify: pinning a lab folder moves it to the sidebar's pinned section; a new category is assignable from the mail context menu.

### AC-01 · Manage accounts and account groups · M

- Scope: accounts list (avatar, colour, status, chevron), Add account entry (starts AC-04), reorder (DL-02), link accounts and edit a linked inbox, the over-the-free-limit upsell row; account group details (name, icon, colour, members add and remove, combine folders, delete group). S06 2.1 rows 1 and 6.
- Mirror: `M:Views/Settings/ManageAccountsPageViewController.cs`, `AccountGroupDetailsPageViewController.cs`; `W:Views/Account/AccountManagementPage`, `AccountGroupDetailsPage`.
- Shared: `AccountManagementViewModel`, `AccountGroupDetailsPageViewModel`, `AccountGroupDetailsItems`, `AccountGroupExpansionStore`, `IAccountService`.
- Files: new `H:Views/Settings/ManageAccountsPage.cs`, `AccountGroupDetailsPage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Accounts.cs`.
- Deps: ST-01, ST-02, DL-02, F-10. Parallel: ST-14, ST-07.
- Shared touch: none.
- Verify: link the two leased accounts; the sidebar shows the merged inbox with the avatar stack and the list shows mail of both with the account colour bar; unlink restores.

### AC-02 · Account details · M

- Scope: tabs General (name, sender name, colour, picture, focused inbox, append sent, initial sync range, delete account, password update, reconnect contacts and tasks, protocol log enable, export, upload), Mail (links to aliases, signatures, special folders, folder customisation, categories, filters, unread badges, notifications, semantic index; jump list row omitted), Calendar, People, To Do (sync enabled, primary calendar, colours, default Show As, DAV sources). S06 2.1 rows 2 to 5.
- Mirror: `M:Views/Settings/AccountDetailsPageViewController.cs`, `M:Views/Onboarding/AccountColorPicker.cs`; `W:Views/Account/AccountDetailsPage`.
- Shared: `AccountDetailsPageViewModel(.Exchange)`, `AccountSettingsWriteLifetime`, `CalendarSettingsSectionViewModelBase`.
- Files: new `H:Views/Settings/AccountDetailsPage.cs`, `AccountDetailsPage.Tabs.cs`, `CG:Settings/WinoColorPicker.cs`.
- Deps: AC-01. Parallel: AC-03, ST-11, ST-12.
- Shared touch: none.
- Verify: rename a leased account and change its colour, sidebar and list follow; delete account removes it and its data folder entries; protocol log export writes a file.

### AC-03 · Alias management · S

- Scope: alias list, primary, reply-to, synchronise aliases, add (DL-02 dialog), delete, copy; the per-alias encryption certificate row appears with PL-05. S06 2.1 "Alias management".
- Mirror: `M:Views/Settings/AliasManagementPageViewController.cs`, `W:Views/Settings/AliasManagementPage`.
- Shared: `AliasManagementPageViewModel`.
- Files: new `H:Views/Settings/AliasManagementPage.cs`.
- Deps: AC-01, DL-02. Parallel: AC-02.
- Shared touch: none.
- Verify: add an alias on a leased account; it is offered in the composer From selector and the sent lab mail carries it.

### AC-04 · Account setup wizard (address-first) · L

- Scope: wizard pages in a navigation view: e-mail entry with provider detection, known-provider chips and catalog search, account name, colour picker, capability picker (Mail, Calendar, Contacts, To Do), mail history range with the "everything" warning, review, advanced setup link; routing to the IMAP, special IMAP, Exchange and OAuth branches (branches land in AC-05, AC-06, AC-09, AC-10); `AddressFirstAccountSetup: true`; `ShowAccountProviderSelectionDialogAsync` real. Entry points: Manage accounts and the Welcome window (ON-01). S06 section 4 rows 2, 3 and 5.
- Mirror: `M:Views/Onboarding/AccountSetupPageViewController.cs`, `.Known.cs`, `.Steps.cs`, `AccountSetupViews.cs`, `AccountColorPicker.cs`, `AccountCapabilityPicker.cs`, `OnboardingForm.cs`; `W:Views/Account/AccountSetupPage`, `AccountSetupKnownChip.cs`, `AccountSetupStepContent.cs`.
- Shared: `AccountSetupPageViewModel.*` (Steps, Routing, ProviderOverride, Features, Review), `AccountCapabilitySelection`, `IKnownImapProviderCatalog`, `IAutoDiscoveryService`.
- Files: new `H:Views/Onboarding/AccountSetupView.cs`, `AccountSetupView.Known.cs`, `AccountSetupView.Steps.cs`, `AccountSetupViews.cs`, `OnboardingForm.cs`, `H:Infrastructure/LinuxPageRegistry.Onboarding.cs`, `LinuxViewModelRegistration.Onboarding.cs`; modify `P:LinuxPlatformRegistration.cs`, `GtkDialogService.Dismissed.cs`.
- Deps: ST-01 (rows), F-07, AC-02 (colour picker control). Parallel: ST-13, ML-06, ML-07.
- Shared touch: none.
- Verify: with AC-05 and AC-06 stubbed as "not built" pages the wizard reaches Review for a lab address and shows the detected IMAP settings; chips filter by search; the capability picker hides Calendar for a mail-only choice.

### AC-05 · IMAP, SMTP, CalDAV and CardDAV setup form · L

- Scope: manual and auto-discovered server form (incoming and outgoing host, port, security, authentication method, separate SMTP credentials, display name, concurrency, advanced options), CalDAV and CardDAV URLs with discovery, standalone DAV account, OAuth choice for providers in `known-imap-providers.json` (uses AC-07), validation progress and failure banner, server certificate trust prompt (exists), special IMAP credentials page (app password and region). S06 section 4 rows 3 (special IMAP), 4, 5 and 8.
- Mirror: `M:Views/Onboarding/ImapCalDavSettingsPageViewController.cs`, `ImapSetupViews.cs`, `SpecialImapCredentialsPageViewController.cs`; `W:Views/Account/ImapCalDavSettingsPage`, `W:Views/SpecialImapCredentialsPage`.
- Shared: `ImapCalDavSettingsPageViewModel.*` (OAuth, DavOAuth, Prefill, Setup), `SpecialImapCredentialsPageViewModel`, `AccountSetupPageViewModel.ImapSignIn.cs`, `.Dav.cs`, `IServerCertificateTrustService`.
- Files: new `H:Views/Onboarding/ImapCalDavSettingsView.cs`, `ImapSetupViews.cs`, `SpecialImapCredentialsView.cs`.
- Deps: AC-04. Parallel: AC-07, ST-13.
- Shared touch: none.
- Verify: add a lab IMAP + SMTP + CalDAV + CardDAV account by typing its settings from the lease file (never logged); wrong password shows the failure banner; the self-signed lab endpoint raises the trust prompt.

### AC-06 · Setup progress, account creation dialog, Exchange form · M

- Scope: progress page (creating, checking incoming and outgoing, success with "Open mailbox", retry, back), `IAccountCreationDialog` (`GetAccountCreationDialog`) with its states, `ShowImapValidationFailedDialogAsync` (error + protocol log + copy), Exchange form (e-mail, server, transport, user, password; the organisation sign-in button uses AC-09), initial synchronisation progress in the shell. Removes the need for `--lab-lease` to get a first account. S06 section 4 rows 7 and 9, section 3 "IMAP validation failed".
- Mirror: `M:Views/AccountSetupProgressPageViewController.cs`, `M:Views/Onboarding/ExchangeSettingsPageViewController.cs`; `W:Views/AccountSetupProgressPage`, `W:Dialogs/AccountCreationDialog`, `ImapValidationFailedDialog`, `W:Views/Account/ExchangeSettingsPage`.
- Shared: `AccountSetupProgressPageViewModel`, `AccountSetupPageViewModel.Progress.cs`, `.Exchange.cs`, `ExchangeSettingsPageViewModel`, `IAccountCreationDialog`, `AccountCreationDialogState`, `IExchangeAutoDiscoveryService`.
- Files: new `H:Views/Onboarding/AccountSetupProgressView.cs`, `ExchangeSettingsView.cs`, `H:Views/Dialogs/AccountCreationDialog.cs`, `ImapValidationFailedDialog.cs`, `H:Infrastructure/GtkDialogService.AccountSetup.cs`.
- Deps: AC-05. Parallel: AC-07, AC-08.
- Shared touch: none.
- Verify: full first-run path in an empty profile on a Release-like run without `--lab-lease`: wizard, IMAP form, progress, "Open mailbox", folders and mail appear. Exchange has no lab account: form checks only, owner tests sign-in.

### AC-07 · Browser sign-in callbacks and brokers · M

- Scope: `LinuxExternalAuthCallbacks` (waiters keyed on `wino://auth-complete` and `wino://auth-complete/oauth`, registered before the browser opens, fed by `LinuxActivation`), `IOAuthBrowserBroker` and `IWinoAccountBrowserAuthBroker` real implementations over `IExternalLauncher`, one sign-in at a time, 5-minute timeout, cancel; `IExternalBrowserAuthenticationPresenter` waiting dialog (cancel, copy URL, launch failed, redirect received). S06 section 5 rows 3, 4 and 6.
- Mirror: `src/Wino.Platform.Windows` `WindowsOAuthBrowserBroker`, `WindowsBrowserAuthBroker`, `WindowsExternalAuthCallbacks`; `src/Wino.Platform.Android` `AndroidExternalAuthCallbacks`; `W:Dialogs/ExternalBrowserAuthenticationDialog`.
- Shared: `IOAuthBrowserBroker`, `IWinoAccountBrowserAuthBroker`, `IExternalBrowserAuthenticationPresenter`, `WinoAccountExternalAuth`, `OAuthTokenService`, `IOAuthTokenStore`.
- Files: rewrite `P:Authentication/LinuxSignInServices.cs` into `P:Authentication/LinuxExternalAuthCallbacks.cs`, `LinuxOAuthBrowserBroker.cs`, `LinuxWinoAccountBrowserAuthBroker.cs`; new `H:Views/Dialogs/ExternalBrowserAuthDialog.cs`, `H:Infrastructure/ExternalBrowserAuthPresenter.cs`; modify `H:Infrastructure/LinuxActivation.cs`, `P:LinuxPlatformRegistration.cs`, `P:AGENTS.md` and `H:AGENTS.md` (the "no sign-in" hard stops).
- Deps: F-07. Parallel: AC-05, AC-06, AC-08.
- Shared touch: none (no client id added).
- Verify: a scratchpad HTTP page that redirects to `wino://auth-complete/oauth?code=test&state=<state>` completes a pending broker call in the running app (logged, code value not secret); cancel and timeout paths return the shared error; a second concurrent call is refused.

### AC-08 · Embedded browser sign-in · S

- Scope: `IEmbeddedBrowserAuthBroker` in the head: a window with a WebKitGTK view on a non-persistent network session, completes and cancels the navigation on the first URI that matches `redirectUri`, close cancels. S06 section 5 row 5.
- Mirror: `M:Infrastructure/MacEmbeddedBrowserAuthBroker.cs`, `M:Views/Dialogs/EmbeddedBrowserSignInSheet.cs`, `W:Services/WindowsEmbeddedBrowserAuthBroker.cs`.
- Shared: `IEmbeddedBrowserAuthBroker`, `EmbeddedBrowserAuthResult`.
- Files: new `H:Infrastructure/GtkEmbeddedBrowserAuthBroker.cs`, `H:Views/Dialogs/EmbeddedBrowserSignInWindow.cs`; modify `EG:WebKitGtkBrowserSession.cs` only if a navigation hook is missing; remove the stub from `P:Authentication/`.
- Deps: AC-07 (same stub file). Parallel: AC-06, AC-09.
- Shared touch: none.
- Verify: Debug action starts the broker on a scratchpad page that redirects to a test redirect URI; the result carries the full callback URI and the window closes; cookies are gone on a second run.

### AC-09 · Outlook sign-in (MSAL host) · M

- Scope: `LinuxOutlookAuthenticationHost`: `PublicClientApplication` for `WinoAuthenticationClientIds.Outlook.Current` (no legacy client on Linux), interactive flow per the owner's answer to Q2 (system browser with loopback redirect, or custom web UI over AC-07), token cache through `MsalCacheHelper` on the Secret Service or the existing protected file store, `CanAuthenticateInteractively`, `RemoveLocalAccountAsync`; Outlook branch of the wizard; Exchange organisation sign-in; re-authentication. S06 section 4 "Outlook", section 5 row 1.
- Mirror: `src/Wino.Platform.MacOS/MacOSOutlookAuthenticationHost.cs`, `src/Wino.Platform.Windows/WindowsOutlookAuthenticationHost.cs`, `src/Wino.Platform.Android` `AndroidOutlookAuthenticationHost`.
- Shared: `IOutlookAuthenticationHost`, `OutlookAuthenticator`, `IAuthenticatorConfig`, `MailAccount.OutlookClientId`.
- Files: new `P:Authentication/LinuxOutlookAuthenticationHost.cs`; modify `P:Wino.Platform.Linux.csproj` (MSAL packages already pinned centrally), `P:LinuxPlatformRegistration.cs` (`OAuthLoopbackRedirect` per Q2).
- Deps: AC-07, AC-04. Blocked on Q2. Parallel: AC-08, AC-10.
- Shared touch: none in code. Owner action: a redirect URI on the Entra app if Q2 needs one.
- Verify: agent: build, the browser opens with the right client id and redirect (URL logged without tokens), cancel returns cleanly. **Owner tests** a real Outlook account: sign-in, first sync, restart with silent token, sign-out.

### AC-10 · Gmail sign-in · S

- Scope: register an `IGoogleAuthorizationCodeReceiver` per the owner's answer to Q1 (the shared loopback `WinoGmailCodeReceiver` with a client the owner names, or a `wino://`-scheme receiver); Gmail branch of the wizard; waiting dialog from AC-07; re-authentication. The Google UWP client `973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn` is not touched, altered or newly referenced by this package unless the owner's answer says so in writing. S06 section 4 "Gmail", section 5 row 2.
- Mirror: `src/Wino.Authentication/WinoGmailCodeReceiver.cs`, `src/Wino.Platform.Apple` `WebSessionGoogleAuthorizationCodeReceiver`.
- Shared: `IGoogleAuthorizationCodeReceiver`, `GmailAuthenticator`, `GoogleAuthorizationPkce`, `IGoogleTokenStore`.
- Files: modify `P:LinuxPlatformRegistration.cs`; new `P:Authentication/LinuxGoogleAuthorizationCodeReceiver.cs` only if the answer needs a custom receiver.
- Deps: AC-07, AC-04. **Blocked on Q1.** Parallel: AC-09.
- Shared touch: T7 only if the owner adds a Linux client id constant to `WinoAuthenticationClientIds`.
- Verify: agent: build, authorization URL shape (client id as decided, PKCE present). **Owner tests** a real Gmail account.

### AC-11 · Re-authentication prompts · S

- Scope: attention row under an account that needs sign-in (sidebar), shell banner with "Sign in", "sign in again" in account details, capability re-auth for contacts and tasks. S06 section 4 last row.
- Mirror: `M:Views/Settings/AccountDetailsPageViewController.cs` (reconnect), macOS sidebar attention row, `W:` shell info bar.
- Shared: `IAccountService` attention state, `FixAccountIssuesMenuItem`, `AccountDetailsPageViewModel`.
- Files: modify `H:Views/Shell/ShellSidebarView.cs`, `H:Views/Settings/AccountDetailsPage.cs`; new `H:Views/Shell/AccountAttentionBanner.cs`.
- Deps: AC-02, AC-07. Parallel: AC-12.
- Shared touch: none.
- Verify: change the leased account's password on the lab (control API), sync: the attention row and banner appear; entering the new password clears them.

### AC-12 · Wino Account sign-in, dialogs and the shell popover · M

- Scope: `ShowWinoAccountLoginDialogAsync`, `ShowWinoAccountRegistrationDialogAsync`, e-mail confirmation required, `ShowWinoAccountExportDialogAsync`, `ShowWinoAccountSyncSecretDialogAsync`; Microsoft, Google and Apple through the API browser flow (AC-07; native providers stay null as on Windows); header account button with avatar and popover (signed out: register and sign in; signed in: name, e-mail, manage, sign out), hidden by the Personalization preference. S01 "Wino Account surface in shell", S06 2.2 rows 1 and 2, section 3.
- Mirror: `M:Views/Account/WinoAccountLoginSheet.cs`, `RegistrationSheet`, `EmailConfirmationSheet`, `SyncSecretSheet`, `M:Views/Extras/WinoAccountPopoverViewController.cs`, `WinoAccountPresenter.cs`, `M:Infrastructure/AppKitDialogService.WinoAccount.cs`; `W:Dialogs/WinoAccount*Dialog`, `W:ShellWindow.xaml` (`WinoAccountFlyout`).
- Shared: `IWinoAccountSessionService`, `IWinoAccountProfileService`, `IWinoAccountApiClient`, `IWinoAccountExternalSignInService`, `WinoAccountProfileUpdatedMessage`.
- Files: new `H:Views/Account/WinoAccountLoginDialog.cs`, `WinoAccountRegistrationDialog.cs`, `WinoAccountEmailConfirmationDialog.cs`, `WinoAccountSyncExportDialog.cs`, `WinoAccountSyncSecretDialog.cs`, `H:Views/Shell/WinoAccountPopover.cs`, `H:Infrastructure/WinoAccountPresenter.cs`, `H:Infrastructure/GtkDialogService.WinoAccount.cs`; modify `H:Views/Shell/ShellHeaderBar.cs`.
- Deps: AC-07, SH-03, F-10. Parallel: AC-11, ST-13.
- Shared touch: none.
- Verify: agent: dialogs open, validation messages, the browser flow starts with `WinoAccountExternalAuth.StartUri`. **Owner tests** a real Wino Account sign-in with e-mail and with one external provider (no lab Wino Account exists).

### AC-13 · Wino Account settings page · M

- Scope: signed-out hero with benefits, sign in and create; signed-in profile (name, photo change and remove), change password, sign out, delete account, add-ons and subscriptions section, sync to account, local export and import entry. S06 2.2 row 1.
- Mirror: `M:Views/Settings/WinoAccountSettings/*` (Profile, SignedOut, AddOns, Sync, BenefitViews); `W:Views/Settings/WinoAccountManagementPage`.
- Shared: `WinoAccountManagementPageViewModel`, `IWinoAccountDataSyncService`, `IWinoAccountIntelligenceSnapshotService`.
- Files: new `H:Views/Settings/WinoAccount/WinoAccountPage.cs`, `WinoAccountSignedOutView.cs`, `WinoAccountProfileView.cs`, `WinoAccountAddOnsView.cs`, `WinoAccountSyncView.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.WinoAccount.cs`.
- Deps: AC-12, ST-02. Parallel: AC-14 (separate files after the add-ons view exists), CA packages.
- Shared touch: none.
- Verify: agent: signed-out layout against the Windows card order (screenshot). Owner: signed-in states.

### AC-14 · Purchases through the Wino Account channel · M

- Scope: `Purchases: true`; `UnlimitedAccountsPurchaseChannel.WinoAccount` as the only channel (`ShowUnlimitedAccountsPurchaseChannelDialogAsync` returns it or shows one row), buy saves a pending checkout and opens the web checkout through `IExternalLauncher`, completion on `wino://` re-activation and on "Refresh purchases" (`RefreshAsync(checkoutCompleted: true)`), the account-limit upsell in Manage accounts, AI pack get button; `LinuxStoreService` stays "no store". S06 2.2 "Purchases".
- Mirror: `M:Views/Account/PurchaseChannelSheet.cs`, the Windows `WinoAccount` channel path in `W:Dialogs/UnlimitedAccountsPurchaseChannelDialog` and its callers.
- Shared: `IWinoPendingCheckoutStore`, `IWinoPurchaseReconciliationService`, `IWinoAccountApiClient`, `UnlimitedAccountsPurchaseChannel`.
- Files: new `H:Infrastructure/GtkDialogService.Purchases.cs`, `H:Infrastructure/WinoAccountCheckout.cs`; modify `H:Views/Settings/WinoAccount/WinoAccountAddOnsView.cs`, `H:Infrastructure/LinuxActivation.cs`, `P:LinuxPlatformRegistration.cs`.
- Deps: AC-13. Needs the answer to Q5. Parallel: CA packages.
- Shared touch: none expected; if the API must learn a Linux client value, that is an API-repository change (handoff).
- Verify: agent: the checkout URL opens and a pending checkout is stored and cleared on refresh. **Owner tests** a real purchase or a test-mode checkout.

### AC-15 · Wino Intelligence settings pages · M

- Scope: Wino Intelligence page (hero, subscription state and usage, summary and translation languages, privacy and consent, daily briefing entry, manage subscription), consent policy dialog, mail classification per account, intelligence coverage and semantic index management (folder picker, date range or latest count, progress, wipe; the coverage band as a small drawn control). S06 2.3 rows 1 to 3, 2.1 "Intelligence coverage".
- Mirror: `M:Views/Settings/WinoIntelligencePageViewController.cs`, `WinoIntelligenceControls.cs`, `IntelligenceConsentPolicySheet.cs`, `IntelligenceSubPages.cs`, `IntelligenceCoveragePageViewController.cs`, `IntelligenceCoverageControls.cs`, `WinoIntelligenceManagementPageViewController.cs`.
- Shared: `WinoIntelligenceManagementPageViewModel.*`, `IntelligenceCoveragePageViewModel`, `IMailIntelligenceStore`, `ISettingsCatalog.CanAccessIntelligence`, `Wino.Core.ML`.
- Files: new `H:Views/Settings/Intelligence/WinoIntelligencePage.cs`, `MailClassificationPage.cs`, `IntelligenceCoveragePage.cs`, `WinoIntelligenceManagementPage.cs`, `IntelligenceConsentDialog.cs`, `CG:Intelligence/WinoCoverageBand.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Intelligence.cs`.
- Deps: AC-13, ST-01, SP-04. Parallel: CA packages.
- Shared touch: none.
- Verify: agent: pages render in the signed-out and no-entitlement states; a scratchpad script confirms the ONNX runtime native asset loads on Linux and one embedding runs (risk R9). Owner: entitled states.

### Phase 7: Calendar

### CA-01 · Calendar mode shell and toolbar · M

- Scope: route for the calendar page, `CalendarAppShellViewModel` host, sidebar calendar list per account (visibility check, colour dot, colour change, context menu), readiness state when no calendar account exists, page toolbar (Today beside the period arrows, range title, Day, Work week, Week, Month switcher at the trailing edge, New event), details zone (320 px) placeholder, sync button state. S04 rows "Calendar list", "View types", "Calendar empty/readiness state".
- Mirror: `M:Views/Calendar/CalendarPageViewController.cs`, `CalendarToolbarView.cs`, `M:Views/Shell/ShellSidebarViewController.cs` (calendar section), `M:Infrastructure/MacPageRegistry.Calendar.cs`; `W:Views/Calendar/CalendarPage.xaml(.cs)`, `W:Controls/Calendar/WinoCalendarTypeSelectorControl.cs`.
- Shared: `CalendarPageViewModel`, `CalendarAppShellViewModel(.ShellMenu, .Synchronization)`, `AccountCalendarViewModel`, `CalendarDisplayType`, `CalendarRangeResolver`, `CalendarCapabilityPresenter`, `CalendarRangeTextFormatter`.
- Files: new `H:Views/Calendar/CalendarPageView.cs`, `CalendarToolbar.cs`, `CalendarReadinessView.cs`, `H:Views/Shell/ShellSidebarView.Calendar.cs`, `H:Infrastructure/LinuxPageRegistry.Calendar.cs`, `LinuxViewModelRegistration.Calendar.cs`.
- Deps: SH-02, SH-04, F-06. Parallel: CA-02, CA-03 (control project).
- Shared touch: none.
- Verify: the leased CalDAV account's calendars list with colours; unchecking one changes `CalendarItems` (logged count); the view switcher changes the range title.

### CA-02 · Mini calendar · M

- Scope: `WinoMiniCalendar`: month grid from `WinoMiniCalendarGrid`, today, selected visible range highlight (day, week or month), per-day event markers, month navigation, first day of week, click navigates the rail; also the date picker popover used by forms (ML-12, CA-10, TD-04). S04 "Mini month picker".
- Mirror: `CA:Shell/WinoMiniCalendarView.cs`, `W:Controls/ShellCalendarView.cs`.
- Shared: `WinoMiniCalendarGrid`, `CalendarPageViewModel` visible range, `CalendarSettings`.
- Files: new `CG:Calendar/WinoMiniCalendar.cs`, `CG:Calendar/WinoDatePickerPopover.cs`; modify `H:Views/Shell/ShellSidebarView.Calendar.cs`.
- Deps: SP-04, F-02. Parallel: CA-01, CA-03.
- Shared touch: T2 (Cairo and Pango package versions, once).
- Verify: screenshot shows the current week highlighted in Week view; clicking a day (Debug action with a date, plus AT-SPI on the day buttons if widgets are used) moves the surface.

### CA-03 · Calendar surface: timed views · L

- Scope: `WinoCalendarSurface` for Day, Work week and Week: header strip, all-day strip with lanes and "n more" overflow popover, 24-hour canvas in a vertical scroller with hour gutter, grid lines and weekend shading (drawn per SP-04), pooled event tiles placed from `CalendarSurfaceLayoutCalculator` through an 80-line frame adapter, tile look (calendar colour, recurring glyph, tentative, busy, declined, past dimming, selected), current-time line on a timer, hour height from rendering settings, initial scroll offset, accessible names from `CalendarAccessibilityFormatter`. Navigation in this package is paged (prev, next, today); the rail comes in CA-04. S04 rows "Time grid", "All-day row", "Overlapping event layout", "Current-time line", "Event rendering".
- Mirror: `CA:Calendar/WinoCalendarSurfaceView.cs`, `.Overflow.cs`, `WinoCalendarItemView.cs`, `CalendarLayoutCalculator.cs`, `M:Views/Calendar/CalendarTileMapper.cs`; `W:Controls/Calendar/CalendarPeriodControl.xaml(.cs)`, `TimedCalendarLayout.cs`, `CalendarItemControl.xaml(.cs)`, `CalendarPeriodControl.CurrentTime.cs`.
- Shared: `CalendarSurfaceLayoutCalculator`, `WinoCalendarSurfaceGeometry`, `CalendarEventDisplay`, `CalendarItemViewModel`, `CalendarItemOverflow`, `CalendarSettings`, `IDateContextProvider`.
- Files: new `CG:Calendar/WinoCalendarSurface.cs`, `WinoCalendarSurface.Timed.cs`, `WinoCalendarSurface.AllDay.cs`, `WinoCalendarSurface.Overflow.cs`, `WinoCalendarTile.cs`, `CalendarLayoutAdapter.cs`; modify `H:Views/Calendar/CalendarPageView.cs`.
- Deps: SP-04, SP-06, F-03, CA-01. Parallel: CA-02; any head-only package outside `Views/Calendar`.
- Shared touch: T2.
- Verify: events seeded on the lab CalDAV calendar (three overlapping, one all-day, one multi-day, one recurring) appear at the right slots (tile allocations from `debug-dump-tree` compared with the calculator's frames); the current-time line sits at now; hour-height setting changes the canvas height; frame-time counter under the SP-04 limits while scrolling.

### CA-04 · Rail navigation and motion · L

- Scope: the infinite horizontal rail per `docs/calendar-rail-navigation.md`: rendered unit window around the position, recycling, per-frame translation from the frame clock, glide curve and snap rules from `CalendarRailMotion`, pan by drag on empty space (6 px dominant-axis threshold), Shift+wheel and touchpad horizontal scroll, the wheel handler on the surface before the scroller, load window requests (`LoadCalendarMessage`), keyboard (Left, Right by period, Home for today), `gtk-enable-animations` honoured. S04 "Rail navigation / loaded event window", "Keyboard shortcuts".
- Mirror: `CA:Calendar/WinoCalendarSurfaceView.Rail.cs`, `.Motion.cs`; `W:Controls/Calendar/CalendarPeriodControl.Rail.cs`, `.Motion.cs`, `.Input.cs`.
- Shared: `CalendarRailSpace`, `CalendarRailMotion`, `CalendarLoadWindow`, `CalendarPageViewModel.LoadedDateWindow`.
- Files: new `CG:Calendar/WinoCalendarSurface.Rail.cs`, `WinoCalendarSurface.Motion.cs`, `WinoCalendarSurface.Input.cs`.
- Deps: CA-03. Parallel: CA-09, CA-10 (head pages).
- Shared touch: none.
- Verify: a Debug action pans the rail by 2.4 units and releases: it snaps to a unit boundary and the title follows; 30 next-steps in a row keep the live tile count bounded (pool size logged); events of the newly loaded window appear; with animations off the move is immediate.

### CA-05 · Month view · M

- Scope: month mode of the surface: vertical week-row rail, day cells with dominant-month dimming, title-only tiles, multi-day bars, "n more" overflow, today marker. S04 "Month grid".
- Mirror: `CA:Calendar/WinoCalendarSurfaceView.cs` (`MonthRailView`), `W:Controls/Calendar/MonthCalendarLayout.cs`.
- Shared: `CalendarSurfaceLayoutCalculator.CalculateMonth`, `MonthGridDates`, `GetDominantMonth`.
- Files: new `CG:Calendar/WinoCalendarSurface.Month.cs`.
- Deps: CA-04. Parallel: CA-09, CA-10, CA-12.
- Shared touch: none.
- Verify: the seeded lab events appear in the right cells; a day with six events shows the overflow link and its popover lists all six.

### CA-06 · Event interactions, context menu and RSVP · M

- Scope: click selects and fills the details zone, double click opens the event, hover state, summary popover anchored to the tile, context menus for an event and for an empty slot (Open, Edit, Delete with this or all events, Respond submenu, Show as submenu, New event here), Delete key, Linux-local copy of the context-menu labels. S04 rows "Event click", "Context menu", "RSVP".
- Mirror: `M:Views/Calendar/CalendarPageViewController.cs` (tapped messages, `NSMenu`, `CalendarContextMenuLabels`), `W:Controls/Calendar/CalendarItemCommandBarFlyout.cs`.
- Shared: `CalendarItemTappedMessage`, `CalendarItemDoubleTappedMessage`, `CalendarItemRightTappedMessage`, `IContextMenuItemService.GetCalendarItemContextMenuItems`, `CalendarContextMenuAction`, `CalendarItemContextActionRequestedMessage`, `RecurringEventDeletion`.
- Files: new `H:Views/Calendar/CalendarPageView.Interaction.cs`, `CalendarContextMenus.cs`, `CalendarEventSummaryPopover.cs`; modify `CG:Calendar/WinoCalendarTile.cs` (events only).
- Deps: CA-03, F-06, F-07. Parallel: CA-04, CA-05.
- Shared touch: none.
- Verify: delete one occurrence of the recurring lab event with "this event" and the series still shows the others (CalDAV server state checked); RSVP on an invited event updates the tile style.

### CA-07 · Drag to create and the quick event popover · M

- Scope: drag on empty grid selects a snapped range (3 px threshold), selection rectangle, click on an all-day or month cell, quick event popover (title, calendar, time summary, Save, More options that opens the composer with the values). S04 "Drag to create".
- Mirror: `CA:Calendar/WinoCalendarSurfaceView.Selection.cs`, `M:Views/Calendar/QuickEventPopover.cs`; `W:Controls/Calendar/CalendarPeriodControl.Selection.cs`, `.HitTesting.cs`.
- Shared: `WinoCalendarSurfaceGeometry` (snapping, `SlotStartAt`), `CalendarSelectionRange`, `CalendarViewDayClickedEventArgs`.
- Files: new `CG:Calendar/WinoCalendarSurface.Selection.cs`, `H:Views/Calendar/QuickEventPopover.cs`.
- Deps: CA-04. Parallel: CA-08 only after both plans agree on the gesture arbitration; otherwise sequential. Parallel with CA-09.
- Shared touch: none.
- Verify: a Debug action selects 10:00 to 11:30 tomorrow; saving from the popover creates the event on the lab CalDAV server with those times.

### CA-08 · Drag to move and resize events · M

- Scope: pointer-driven move (4 px threshold) with a ghost tile, resize from the bottom 6 px with the resize cursor, snapping, move between days and to and from the all-day strip, commit through the shared request pipeline with revert on failure, Esc cancels. S04 "Drag to move / resize".
- Mirror: `CA:Calendar/WinoCalendarSurfaceView.Drag.cs`, `W:Controls/Calendar/CalendarItemControl.xaml.cs` (`CalendarDragPackage`).
- Shared: `WinoCalendarDragTarget`, `WinoCalendarSurfaceGeometry`, `CalendarItemDroppedEventArgs`.
- Files: new `CG:Calendar/WinoCalendarSurface.Drag.cs`.
- Deps: CA-07. Parallel: CA-09, CA-10, CA-11.
- Shared touch: none.
- Verify: a Debug action moves a lab event by +1 day and +2 hours and resizes it by +30 minutes; the CalDAV server holds the new times; a move on a read-only calendar shows the read-only message and reverts.

### CA-09 · Event details · M

- Scope: details zone and standalone window: title, time and recurrence summary, calendar, location with join link, attendees with RSVP state, notes rendered in the reader session, attachments, reminders, actions (Edit, Delete, Respond, Open in window). S04 "Event details page".
- Mirror: `M:Views/Calendar/EventDetailsPageViewController.cs`, `EventDetailsWindow.cs`; `W:Views/Calendar/EventDetailsPage.xaml(.cs)`.
- Shared: `EventDetailsPageViewModel`, `CalendarNotesHtmlResolver`, `CalendarEventDisplay`, `CalendarJoinLinkResolver`.
- Files: new `H:Views/Calendar/EventDetailsView.cs`, `EventDetailsView.Sections.cs`.
- Deps: CA-01, SH-07, RD-03 (attachment tiles). Parallel: CA-04, CA-05, CA-07, CA-08.
- Shared touch: none.
- Verify: a lab event with location, two attendees and HTML notes shows every section; the join link opens through the launcher (logged).

### CA-10 · Event composer · M

- Scope: create and edit: title, start and end with date picker (CA-02 popover) and a `WinoTimePicker` control (GTK has none), all-day, time zone, location, calendar picker, Show as, reminders, online meeting toggle, attachments with drop, notes through ED-11, Save and Discard with confirmation, as an in-pane page and as a window. S04 "Create / edit event", "Description / notes editor", "Time zones".
- Mirror: `M:Views/Calendar/CalendarEventComposePageViewController.cs`, `.Drop.cs`; `W:Views/Calendar/CalendarEventComposePage.xaml(.cs)`.
- Shared: `CalendarEventComposePageViewModel`.
- Files: new `H:Views/Calendar/CalendarEventComposeView.cs`, `CalendarEventComposeView.Fields.cs`, `CG:Calendar/WinoTimePicker.cs`.
- Deps: CA-02, ED-11, DL-01 (calendar picker). Parallel: CA-04, CA-05, CA-08.
- Shared touch: none.
- Verify: create an event with a reminder and notes on the lab calendar, reopen it, change the time, save; the server holds the change.

### CA-11 · Recurrence editor and attendees · M

- Scope: recurrence presets, custom rule (interval, unit, weekday toggles, month options), end (never, count, date), summary text; attendee field with suggestions (reuses ED-03 and ED-04 controls), required and optional, remove. S04 "Recurrence editor", "Attendees with autocomplete".
- Mirror: `M:Views/Calendar/CalendarEventComposePageViewController.Recurrence.cs`, `.Attendees.cs`.
- Shared: `CalendarEventComposePageViewModel` (recurrence and attendee members), the contact suggestion service.
- Files: new `H:Views/Calendar/CalendarEventComposeView.Recurrence.cs`, `CalendarEventComposeView.Attendees.cs`.
- Deps: CA-10, ED-04. Parallel: CA-08, CA-12.
- Shared touch: none.
- Verify: a weekly event on Monday and Wednesday for 6 occurrences shows 6 tiles; inviting the second leased account delivers an invitation mail that RD-06 renders.

### CA-12 · Calendar search · S

- Scope: header search for events with a suggestion list, navigate to the chosen event. S04 "Search events".
- Mirror: `M:Views/Calendar/CalendarPageViewController.Search.cs`, `M:Views/Shell/ShellSearchSuggestionList.cs`.
- Shared: `CalendarPageViewModel` search members.
- Files: new `H:Views/Calendar/CalendarPageView.Search.cs`, `H:Views/Shell/SearchSuggestionList.cs`.
- Deps: SH-03, CA-03. Parallel: CA-05, CA-11.
- Shared touch: none.
- Verify: typing part of a seeded event title lists it; choosing it moves the surface to its day and selects it.

### Phase 8: Contacts

### CT-01 · Contacts page, sidebar and list · M

- Scope: route, list and detail split with compact swap, sidebar rows for address books and lists per account with New contact, New list, New address book and rename and delete list menus, contact list with letter sections (SP-02), search, favourites, paged load-more, `WinoContactRow`, sync button and error banner, empty states. S05 Contacts rows 1 to 3, "Address book sync".
- Mirror: `M:Views/Contacts/ContactsPageViewController.cs`, `.List.cs`, `CA:Contacts/WinoContactRowView.cs`, `M:Infrastructure/MacPageRegistry.Contacts.cs`; `W:Views/Settings/ContactsPage.xaml(.cs)`.
- Shared: `ContactsPageViewModel`, `.ShellMenu.cs`, `.Synchronization.cs`, `LoadMoreContactsCommand`, `CreateAddressBookCommand`.
- Files: new `H:Views/Contacts/ContactsPageView.cs`, `ContactsPageView.List.cs`, `CG:Contacts/WinoContactRow.cs`, `H:Views/Shell/ShellSidebarView.Contacts.cs`, `H:Infrastructure/LinuxPageRegistry.Contacts.cs`, `LinuxViewModelRegistration.Contacts.cs`.
- Deps: SH-02, SH-04, F-04, F-10, DL-01. Parallel: CA-06 to CA-12, TD-01.
- Shared touch: none.
- Verify: contacts seeded on the lab CardDAV account list under their letters; search narrows; creating a list shows it in the sidebar.

### CT-02 · Contact detail, actions and multi-select · M

- Scope: detail pane (header picture, field cards for e-mail, phone, personal, birthday, website, notes; copy buttons), actions (send mail through the composer, edit, favourite, delete with confirmation, add to and remove from list), context menu, select mode with bulk delete, favourite and add to list. S05 Contacts rows 4 to 8, 10, 11.
- Mirror: `M:Views/Contacts/ContactsPageViewController.Detail.cs`, `CA:Contacts/WinoContactFieldCard.cs`, `W:Controls/ContactCardMenuFlyout.cs`.
- Shared: `SelectedContact`, `ComposeToContactCommand`, `ToggleFavoriteCommand`, `DeleteContactCommand`, `DeleteSelectedContactsCommand`, `IsSelectionMode`, `AddContactCommand`.
- Files: new `H:Views/Contacts/ContactsPageView.Detail.cs`, `ContactsPageView.Selection.cs`, `ContactContextMenu.cs`, `CG:Contacts/WinoContactFieldCard.cs`.
- Deps: CT-01, ED-02 (send mail). Parallel: CT-03, TD-01.
- Shared touch: none.
- Verify: delete a seeded lab contact (gone from the CardDAV server); "Send mail" opens the composer with the address as a chip.

### CT-03 · Contact editor · L

- Scope: edit page with the category tabs of `ContactEditorCategory`: identity (display, given, surname, nickname, file-as), repeated e-mails with label, phones with kind, IM addresses, relations and children, work (company, department, title, office), addresses, birthday, website, notes, list membership, mail categories, save-to destination; photo choose and remove; save, back with unsaved-changes confirmation; create from the destination picker; import draft from a `.vcf`. Repetitive form; one subsystem. S05 "Contact edit page", "Contact photo", "Create contact".
- Mirror: `M:Views/Contacts/ContactEditPageViewController.cs`, `W:Views/Settings/ContactEditPage.xaml(.cs)`.
- Shared: `ContactEditPageViewModel`, `ContactEditorCategory`, `ContactEditNavigationParameter`.
- Files: new `H:Views/Contacts/ContactEditView.cs`, `ContactEditView.Identity.cs`, `ContactEditView.Communication.cs`, `ContactEditView.Details.cs`, `CG:Contacts/WinoRepeatedFieldGroup.cs`.
- Deps: CT-01, ST-01 (rows), F-07. Parallel: CT-02, TD-02.
- Shared touch: none.
- Verify: create a contact with two e-mails, a phone, a birthday and a photo on the lab CardDAV book; the vCard on the server carries every field (fetched by a scratchpad script over the real DAV client).

### CT-04 · Contact drag to list and vCard file activation · S

- Scope: drag contacts onto a list row in the sidebar (SP-07 payload), `text/vcard` in the desktop entry and `GApplication.Open` routing to the import draft. S05 "Drag contact onto a list", "vCard import".
- Mirror: `M:Views/Contacts/ContactDragPayload.cs`, `W:` contact list drag handlers.
- Shared: `ContactDragPackage`, `IActivationFileImportService`, `IVCardCodec`.
- Files: new `H:Views/Contacts/ContactDragPayload.cs`, `ContactsPageView.Drag.cs`; modify `H:Resources/app.winomail.Mail.desktop`, `H:Infrastructure/LinuxActivation.cs`.
- Deps: CT-03, ML-10 (sidebar drop plumbing). Parallel: TD packages.
- Shared touch: T5.
- Verify: `gio open contact.vcf` opens the editor prefilled; the drop path through a Debug action adds the contact to the list.

### Phase 9: To Do

### TD-01 · To Do page, sidebar and list management · M

- Scope: route, task surface with the 340 px detail drawer and compact swap below 1008 px, sidebar smart lists (My Day, Important, Planned) and lists per account with counts, list header with editable title, create, rename, delete, e-mail list (composer), print list (through the print presenter), sync and error states, empty state. S05 To Do rows 1 to 3, "Task completion notifications / sync".
- Mirror: `M:Views/ToDo/ToDoPageViewController.cs`, `ToDoPage.cs`, `M:Infrastructure/MacPageRegistry.Tasks.cs`; `W:Views/ToDo/ToDoPage.xaml(.cs)`.
- Shared: `ToDoPageViewModel`, `.Synchronization.cs`, `TaskViewKind`, `CreateListAsync`, `RenameSelectedListAsync`, `DeleteListAsync`, `PrintList`, `EmailList`.
- Files: new `H:Views/ToDo/ToDoPageView.cs`, `ToDoListHeader.cs`, `H:Views/Shell/ShellSidebarView.Tasks.cs`, `H:Infrastructure/LinuxPageRegistry.Tasks.cs`, `LinuxViewModelRegistration.Tasks.cs`.
- Deps: SH-02, SH-04, F-07, RD-05 (print), ED-02 (e-mail list). Parallel: CT-01, CT-02, TD-02 (control project).
- Shared touch: none.
- Verify: the leased account's task lists (CalDAV VTODO on the lab) appear with counts; create, rename and delete a list on the server.

### TD-02 · Task list and task card · L

- Scope: grouped task list with section headers (SP-02), `WinoTaskCard` (round check box, title, importance star, due chip with overdue colour, steps count, My Day marker, completed style), quick add entry with due chip menu, toggle complete and importance, completion sound hook, virtualised. S05 "Task list view", "Quick add composer".
- Mirror: `M:Views/ToDo/ToDoPageViewController.List.cs`, `CA:ToDo/WinoTaskCardView.cs`, `WinoRoundCheckbox.cs`, `WinoChipButton.cs`, `WinoToDoStyle.cs`, `WinoQuickAddView.cs`.
- Shared: `ToDoPageViewModel` task groups, `ToggleTaskCommand`, `ToggleImportanceCommand`, `AddTaskCommand`, `ITaskCompletionSound`.
- Files: new `CG:ToDo/WinoTaskCard.cs`, `WinoRoundCheckbox.cs`, `WinoChipButton.cs`, `WinoQuickAdd.cs`, `H:Views/ToDo/ToDoPageView.List.cs`.
- Deps: F-04, SP-02, TD-01. Parallel: CT-03, CT-04.
- Shared touch: none.
- Verify: add a task through quick add (AT-SPI text + Debug commit), complete it, mark it important; the VTODO on the lab server reflects each change.

### TD-03 · Filters, sort, search, suggestions, menus and keys · M

- Scope: filter text, completion scope, important filter, sort menu, clear filters, compact options, suggestions popover with "add to My Day", task context menu (move to list, My Day, importance, complete, due presets, delete), keyboard (Enter in quick add, Delete, Ctrl+F). S05 rows "Filter / sort / search row", "Suggestions", "Context menus on tasks", "Keyboard shortcuts".
- Mirror: `M:Views/ToDo/ToDoPageViewController.Search.cs`, `.List.cs` (menus); `W:Views/ToDo/ToDoPage.xaml` flyouts.
- Shared: `SetSort`, `SetCompletionScope`, `ClearFilters`, `SetDuePreset`, `OpenSuggestions`, `AddSuggestionToMyDayCommand`.
- Files: new `H:Views/ToDo/ToDoFilterBar.cs`, `ToDoSuggestionsPopover.cs`, `ToDoContextMenu.cs`.
- Deps: TD-02, F-06. Parallel: TD-04.
- Shared touch: none.
- Verify: "Important only" hides the others; sort by due date reorders; "Add to My Day" from suggestions shows the task under My Day.

### TD-04 · Task detail drawer, steps and bulk actions · M

- Scope: drawer rows: title, notes, due date (presets and the CA-02 date popover), importance, My Day, move to list (DL-01 picker), status summary, create calendar event, delete, close; steps (add, toggle, rename, delete); reminder and recurrence rows shown disabled as on Windows; multi-select bar (complete, reopen, important, delete). S05 rows "Task detail pane", "Steps", "Reminders and recurrence", "Create calendar event from task", "Multi-select bulk bar".
- Mirror: `M:Views/ToDo/ToDoPageViewController.Detail.cs`, `CA:ToDo/WinoDrawerRows.cs`.
- Shared: `SaveTaskAsync`, `DeleteTaskCommand`, `ToggleMyDay`, `AddStepAsync`, `ToggleStepAsync`, `SaveStepAsync`, `DeleteStepAsync`, `CreateCalendarEventCommand`, `CompleteSelected`, `ReopenSelected`.
- Files: new `H:Views/ToDo/ToDoDetailDrawer.cs`, `ToDoDetailDrawer.Steps.cs`, `ToDoSelectionBar.cs`, `CG:ToDo/WinoDrawerRows.cs`.
- Deps: TD-02, CA-02, DL-01; CA-10 for "create calendar event". Parallel: TD-03.
- Shared touch: none.
- Verify: set a due date and two steps on a lab task, complete one step; the server VTODO holds the due date; "Create calendar event" opens the event composer prefilled.

### TD-05 · Task reorder and drag to list · M

- Scope: drag reorder inside a list with a drop indicator line (manual hit test by row), drag a task onto a sidebar list to move it, keyboard alternative Move up and Move down in the menu. S05 "Drag reorder tasks".
- Mirror: `M:Views/ToDo/ToDoPageViewController.Reorder.cs`, `M:Views/Shell/ShellSidebarViewController.TaskListDrag.cs`.
- Shared: `CanReorderTasks` and the reorder command.
- Files: new `H:Views/ToDo/ToDoPageView.Reorder.cs`, `H:Views/Shell/ShellSidebarView.TaskDrop.cs`.
- Deps: SP-07, TD-03, ML-10. Parallel: PL packages.
- Shared touch: none.
- Verify: Move up through the menu changes the order and it survives a re-sync; the drop path through a Debug action moves a task to another list on the server.

### Phase 10: intelligence surfaces

All three need a signed-in Wino Account with the entitlement (AC-12), so the entitled states are owner checks.

### IN-01 · Intelligence header in the reader · L

- Scope: `WinoIntelligenceHeader`: collapsed and expanded card, progress ring, tabs and panels (summary, translation, tiles), actions, error and consent states, translated body through `ResolveActiveHtmlAsync`. S02 1.5 "Intelligence header".
- Mirror: `CA:Intelligence/WinoIntelligenceHeaderView.cs`, `.Panels.cs`, `M:Views/Mail/MailRenderingPageViewController.Intelligence.cs`; `W:` `controls/Wino.Mail.Controls.WinUI/IntelligenceHeader/*`.
- Shared: `WinoIntelligenceHeaderPresenter`, `WinoIntelligenceHeaderViewState`, `IntelligenceHeaderModels`, `docs/mail-intelligence.md`.
- Files: new `CG:Intelligence/WinoIntelligenceHeader.cs`, `WinoIntelligenceHeader.Panels.cs`, `WinoIntelligenceProgressRing.cs`, `H:Views/Mail/MailReaderView.Intelligence.cs`.
- Deps: RD-01, AC-12, AC-15. Parallel: IN-02, PL packages.
- Shared touch: none.
- Verify: agent: every view state rendered from a Debug gallery that feeds the control the shared state objects (screenshots light and dark). Owner: live summary and translation.

### IN-02 · Daily briefing panel · M

- Scope: trailing panel (360 px, resizable, overlay when narrow) toggled from the header with the unseen dot, date pager, briefing cards, privacy line, open-item navigation; relay introduction tip and relay fallback indicator placeholders show the shared "unsupported" state while push relay is deferred. S01 "Daily briefing panel", S02 1.5 "Daily briefing panel".
- Mirror: `M:Views/Extras/DailyBriefingPanelViewController.cs`, `DailyBriefingPresenter.cs`, `CA:Extras/WinoBriefingCardView.cs`, `WinoDatePager.cs`; `W:Controls/DailyBriefingPanel.xaml(.cs)`.
- Shared: `DailyBriefingPanelViewModel`, `DailyBriefingCardContent`, `DailyBriefingItem`, `DailyBriefingStateChanged`, `DailyBriefingIcons`.
- Files: new `H:Views/Extras/DailyBriefingPanel.cs`, `H:Infrastructure/DailyBriefingPresenter.cs`, `CG:Extras/WinoBriefingCard.cs`, `WinoDatePager.cs`; modify `H:Views/Shell/ShellHeaderBar.cs`, `ShellLayout.cs`.
- Deps: SH-03, AC-12. Parallel: IN-01, IN-03.
- Shared touch: none.
- Verify: agent: panel toggles, empty and signed-out states. Owner: a real briefing.

### IN-03 · Composer rewrite assist · M

- Scope: rewrite button with the modes menu, status strip (keep, regenerate, cancel, toggle original), the animated intelligence ring. S03 "Rewrite / Intelligence assist".
- Mirror: `M:Views/Mail/ComposePageViewController.Rewrite.cs`, `M:Views/Mail/Compose/ComposeIntelligenceRing.cs`; `W:Views/Mail/ComposePage.xaml` (`ComposeRewritePanel`).
- Shared: `ComposerRewriteSession`, `RefreshRewriteAvailabilityAsync`.
- Files: new `H:Views/Mail/Compose/ComposeView.Rewrite.cs`, `CG:Intelligence/WinoIntelligenceRing.cs`.
- Deps: ED-02, AC-12. Parallel: IN-01, IN-02.
- Shared touch: none.
- Verify: agent: button hidden without the entitlement, strip states from a Debug gallery. Owner: a real rewrite.

### Phase 11: app lock, notifications, startup, onboarding, What's New, polish

### PL-01 · App lock (PIN) · M

- Scope: `LinuxAppLockPlatform` (`IsAvailable` true, biometrics false), lock screen that replaces the window content (PIN entry, error, attempts), `PromptVerificationAsync` dialog, lock on start and after the configured idle time, secondary windows hidden while locked, notification content hidden while locked, App lock settings page (set, change, remove PIN; the biometrics row omitted), `AppLock: true`. S01 "App lock", S06 2.4 "App lock".
- Mirror: `W:Views/AppLockScreenPage.xaml(.cs)`, `W:Dialogs/AppLockVerificationDialog.xaml(.cs)`, `W:Views/Settings/AppLockPage`, `W:ShellWindow.xaml.cs` (`ShowAppLock`, `RunWhenUnlockedAsync`). macOS has no app lock.
- Shared: `IAppLockService`, `IAppLockPlatform`, `AppLockPageViewModel`.
- Files: new `P:Security/LinuxAppLockPlatform.cs`, `H:Views/AppLock/AppLockScreen.cs`, `AppLockVerificationDialog.cs`, `H:Views/Settings/AppLockPage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.AppLock.cs`; modify `H:Views/MainWindow.cs`, `P:LinuxPlatformRegistration.cs`.
- Deps: ST-02, SH-07. Parallel: PL-02, PL-03.
- Shared touch: none.
- Verify: set a PIN, restart: the lock screen shows and `debug-dump-tree` contains no mail row; a wrong PIN is refused; the right PIN unlocks; a notification activation while locked waits for the unlock.

### PL-02 · Notifications: actions, reminders and the rest · M

- Scope: `LinuxNotificationBuilder` complete: new-mail buttons from the two configured actions routed through `app.*` actions to `INotificationResponseService`, default action opens the message, sender picture as a temporary PNG icon, calendar reminder with Snooze and Dismiss, task notifications, attention-required, test notifications, withdraw on read; `NotificationActions: true`; badge methods stay no-ops (section 4); presence provider stays "not presenting" (section 4). S01 "Notifications", S04 "Reminders / notifications", S07 rec. 12.
- Mirror: `src/Wino.Platform.Apple/Services/AppleNotificationBuilder.cs`, `W:Services/NotificationBuilder.cs`, `W:Activation/AppNotificationHandler`.
- Shared: `INotificationBuilder`, `INotificationResponseService`, `INotificationPolicyService`, `INotificationResponseNavigator`, `CalendarReminderNotificationRequest`.
- Files: rewrite `P:Notifications/LinuxNotificationBuilder.cs`; new `P:Notifications/LinuxNotificationActions.cs`, `H:Infrastructure/NotificationActions.cs`; modify `H:LinuxApplication.cs`, `P:LinuxPlatformRegistration.cs`.
- Deps: SH-06 (app actions), ST-05 for the settings. Parallel: PL-01, PL-03.
- Shared touch: none.
- Verify: `deliver` on the lab: the notification carries two buttons (`gdbus monitor`), invoking "Mark as read" through `org.freedesktop.Notifications` `ActionInvoked` (sent with `gdbus emit` in the private session) marks the lab message read; a lab event starting in two minutes raises a reminder.

### PL-03 · Startup, background and close behaviour · M

- Scope: `LinuxStartupIntegrationService` (Background portal `RequestBackground` with autostart through `Gio.DBusConnection`, and the `~/.config/autostart` desktop file outside a sandbox), `--background` start without a window, close behaviour preference (quit, or hide and keep synchronising through `Application.Hold`; reopened by launching again or by a notification), `StartupIntegration: true`; the General settings rows become live. S01 "Tray / background behaviour" (background part), "Autostart".
- Mirror: `src/Wino.Platform.MacOS` `MacStartupIntegrationService`, `W:App.Background.cs`, `W:ShellWindow.xaml.cs` (`OnAppWindowClosing`).
- Shared: `IStartupIntegrationService`, `IPreferencesService` close behaviour, `ApplicationLifecycleCoordinator`.
- Files: new `P:Services/LinuxStartupIntegrationService.cs`, `P:Native/BackgroundPortal.cs`; modify `H:LinuxApplication.cs`, `H:Program.cs`, `P:LinuxPlatformRegistration.cs`.
- Deps: ST-03. Parallel: PL-01, PL-02, PL-04 (PL-04 owns the desktop entry; PL-03 only reads its path).
- Shared touch: none.
- Verify: enabling launch at sign-in writes the autostart file (or the portal grants it); with "keep running" a closed window still raises a notification on `deliver` and a second launch shows the window again; `--background` shows no window (AT-SPI: no frame).

### PL-04 · Desktop integration and activation · M

- Scope: installed desktop entry with `MimeType` for `x-scheme-handler/wino`, `x-scheme-handler/mailto`, `message/rfc822`, `text/calendar`, `text/vcard`; desktop actions (New mail, New event, Calendar, Contacts, To Do) as the jump-list equivalent; command line (`--mode`, `--compose`, a `mailto:` URI); `mailto:` opens the composer prefilled; `.ics` file import; application icon in the hicolor theme layout; AppStream metainfo; `StartupWMClass`. S01 "Startup / activation", S07 packaging note.
- Mirror: `M:Infrastructure/MacActivationCoordinator.cs`, `M:AppDelegate.Activation.cs`, `W:Activation/*`, `W:Package.appxmanifest`.
- Shared: `ActivationRouter`, `ActivationUriClassifier`, `MailToUri`, `MailtoProtocolMessageRequested`, `IActivationFileImportService`.
- Files: modify `H:Resources/app.winomail.Mail.desktop`, `H:Infrastructure/LinuxActivation.cs`, `H:Program.cs`, `H:LinuxApplication.cs`; new `H:Resources/app.winomail.Mail.metainfo.xml`, `H:Resources/icons/*`.
- Deps: ED-02 (mailto), CA-10 (ics), RD-07, CT-04. Parallel: PL-03, WN-01.
- Shared touch: T5.
- Verify: `desktop-file-validate`; `gio open mailto:someone@example.com?subject=Hi` opens a composer with the chip and subject in the running instance; the New mail desktop action (`gio launch` or `gapplication action`) opens a composer; `appstreamcli validate`.

### PL-05 · S/MIME · L

- Scope: `LinuxSmimeCertificateService` over the per-user .NET `X509Store` (My and AddressBook), import of `.pfx`/`.p12`/`.cer`, export, remove; Signature and encryption settings page; composer sign and encrypt toggles with certificate pickers; reader signature and encryption indicators with certificate details; per-alias signing certificate row; `Smime: true`. S02 1.5 "Encryption / signature indicators", S03 "S/MIME sign/encrypt", S06 2.5 "Signature and encryption". The store choice is open question Q7.
- Mirror: `src/Wino.Platform.Windows/Services/SmimeCertificateService.cs`, `src/Wino.Platform.MacOS/Services/MacSmimeCertificateService.cs`, `M:Infrastructure/MacPageRegistry.Settings.Smime.cs` and its page, `M:Views/Mail/ComposePageViewController.Options.cs`; `W:Views/Settings/SignatureAndEncryptionPage`.
- Shared: `ISmimeCertificateService`, `SmimeCertificatePurpose`, the composer's `IsSmimeSignatureEnabled` members, `ShowSmimeSigningCertificateInfo`.
- Files: new `P:Security/LinuxSmimeCertificateService.cs`, `H:Views/Settings/SignatureAndEncryptionPage.cs`, `H:Infrastructure/LinuxPageRegistry.Settings.Smime.cs`, `H:Views/Mail/Compose/ComposeView.Smime.cs`, `H:Views/Mail/MailReaderHeader.Smime.cs`; modify `P:LinuxPlatformRegistration.cs`, `H:Views/Settings/AliasManagementPage.cs`.
- Deps: ED-02, RD-01, AC-03, ST-02. Parallel: PL-01 to PL-04.
- Shared touch: none.
- Verify: import a self-signed test certificate for both leased accounts (generated in the scratchpad), send signed and encrypted mail between them; the reader shows "signed" and decrypts; a tampered message shows the invalid state.

### PL-06 · Small platform services · S

- Scope: task completion sound (libcanberra event through `LibraryImport`, silent when the library is missing), `LaunchFolderAsync` showing the item in its folder, clipboard image and file support if the interface asks for it, `Gio.NetworkMonitor` hook only if a shared contract already consumes connectivity (no new shared contract), raise the remaining honest capability flags. S07 section 2 rows "ITaskCompletionSound", "IExternalLauncher", "IClipboardService".
- Mirror: `src/Wino.Platform.MacOS` `MacTaskCompletionSound`, `MacExternalLauncher`.
- Shared: `ITaskCompletionSound`, `IExternalLauncher`, `IClipboardService`.
- Files: modify `P:Services/LinuxPlatformServices.cs`; new `P:Native/Canberra.cs`.
- Deps: none. Parallel: any.
- Shared touch: none.
- Verify: completing a task plays the sound (`pactl list sink-inputs` or the PipeWire equivalent shows a stream, or the owner listens); "Open folder" on Storage opens the file manager.

### ON-01 · Welcome window and first run · S

- Scope: first-run startup target: welcome page (hero, Add account, Wino Account hint), hosts AC-04 in the same window, then swaps to the shell; no account and all-accounts-removed states; `--lab-lease` stays Debug only. S01 "Onboarding / Welcome", S06 section 4 row 1.
- Mirror: `M:WelcomeWindow.cs`, `M:Views/WelcomePageV2.cs`, `WelcomePageV2ViewController.cs`; `W:WelcomeWindow.xaml(.cs)`, `W:Views/WelcomePageV2`.
- Shared: `WelcomePageV2ViewModel`, `WelcomeHostPageViewModel`, `ApplicationStartupTarget`, `ApplicationLifecycleCoordinator.ShowStartup`.
- Files: new `H:Views/Onboarding/WelcomeView.cs`; modify `H:Infrastructure/GtkApplicationHosts.cs`, `H:Views/MainWindow.cs`.
- Deps: AC-04, AC-06. Parallel: WN-01, PL packages.
- Shared touch: none.
- Verify: an empty profile starts on Welcome; adding a lab IMAP account through the wizard lands in the shell; removing the last account returns to Welcome.

### WN-01 · What's New · S

- Scope: What's New window (releases with illustration, title and items; previous and next), header button shown by `ShouldShowShellEntryAsync`, primary-menu entry, assets linked from `W:Assets/WhatsNew` next to the binary. S01 "What's New".
- Mirror: `M:Views/Extras/WhatsNewPageViewController.cs`, `WhatsNewPresenter.cs`, `WhatsNewWindowController.cs`; `W:WhatsNewWindow.xaml(.cs)`.
- Shared: `IWhatsNewService`, `WhatsNewPageViewModel`, `WhatsNewOpened`, `WhatsNewRequested`.
- Files: new `H:Views/Extras/WhatsNewWindow.cs`, `H:Infrastructure/WhatsNewPresenter.cs`; modify `H:Wino.Mail.Linux.csproj`, `H:Views/Shell/ShellHeaderBar.cs`.
- Deps: SH-03, SH-07. Parallel: ON-01, PL packages.
- Shared touch: none (assets linked).
- Verify: the window shows the latest release JSON with its image; after opening, the header button hides until a newer version.

### PO-01 · Accessibility and keyboard traversal audit · M

- Scope: every icon-only button and custom control has an accessible label and role, list rows have a composed name (sender, subject, date, state), focus order per page, focus return after dialogs and popovers, Tab traversal in forms, high-contrast check, Orca smoke test of mail list, reader, composer and settings.
- Mirror: `M:` accessibility labels, `CalendarAccessibilityFormatter`, `ReaderAccessibilityContext`.
- Files: across `H:Views/**` and `CG:**` (labels only).
- Deps: all UI phases. Parallel: none with UI packages; PO-03 (docs).
- Shared touch: T1 if a label has no string.
- Verify: an AT-SPI script walks every page and reports zero unnamed interactive widgets.

### PO-02 · Adaptive, performance and leak pass · M

- Scope: every page at 1360, 1008, 640 and 420 px wide; mail list with 20,000 rows and a sync burst; calendar frame times; live-object counts after opening and closing each page, window and dialog 20 times; cold start time of the AOT binary; right-to-left screenshots. Fixes stay inside the offending files.
- Mirror: macOS verification list in `docs/macos-design-decisions.md`.
- Files: as found.
- Deps: all UI phases. Parallel: PO-03.
- Shared touch: none.
- Verify: a table of measurements in the package report; no page clips or scrolls horizontally at 420 px.

### PO-03 · Documentation and release readiness · S

- Scope: `docs/linux-design-decisions.md` (shell, mail workflow, control choices, accepted deviations from section 4), rewrite `H:AGENTS.md`, `P:AGENTS.md`, `PG:` and `CG:` `AGENTS.md` and `docs/harness/development-linux.md` (no longer a proof of concept; AT-SPI and Debug-action checks; Enchant and font notes), root `AGENTS.md` rows; trimmed and AOT publishes run and exercised; Sentry address decision (Q12); packaging per Q8.
- Deps: everything. Parallel: PO-01, PO-02.
- Shared touch: T3, T6.
- Verify: links valid; both publishes start, sync a lab account and open a message.

### TP-01 · Tiling desktop profile and the Omarchy theme · M

- Scope: issue #1216 follow-up on `feature/1216-omarchy`. Detection of tiling compositors and Omarchy from environment variables and state files (`Wino.Platform.Linux/Desktop`), the Window style and Follow the Omarchy theme settings (`LinuxDesktopProfile`), window buttons for all header bars through `gtk-decoration-layout` (`WindowChrome`), the flat tiling look and the Omarchy palette in `ThemeCss`, a live theme watcher (`OmarchyThemeWatcher`), the Wayland app id for every window (`g_set_prgname`), and small-tile fixes (onboarding footers, wrapping action rows).
- Decisions: [Linux design decisions](linux-design-decisions.md#tiling-profile-and-omarchy-theme-22-november-2026).
- Shared touch: additive `Linux_` strings only.
- Verify: Debug build with 0 warnings; on Omarchy (Hyprland) `debug-desktop-report` with Tiling and the palette applied, a light and a dark Omarchy theme switch followed live, the Standard path's theme CSS equal to the code before, no OVERFLOW at 460 × 490 in onboarding and Settings, every window with the app id `app.winomail.Mail`. Release gate pending.

ML-07 (swipe actions, defined in Phase 3) is scheduled in this phase.

### Phase 12: needs owner confirmation before planning (open question Q6)

### VC-01 · Microphone service · L

- Scope: `IMicrophoneService` over PipeWire or PulseAudio (device list, default device, level meter, capture stream, permission through the portal in a sandbox). S07 section 2 "IMicrophoneService".
- Mirror: `src/Wino.Platform.Apple` `AppleMicrophoneService` (611 lines), the Windows microphone service.
- Shared: `IMicrophoneService`, `VM:Voice/*`.
- Files: new `P:Voice/LinuxMicrophoneService.cs`, `P:Native/PulseAudio.cs`.
- Deps: none. Parallel: any.
- Shared touch: none.
- Verify: device list matches `pactl list sources short`; a 3-second capture has a non-zero level.

### VC-02 · Voice control UI, voice settings and the global hotkey · L

- Scope: voice bubble, Voice control and Voice brain settings pages, global hotkey through `org.freedesktop.portal.GlobalShortcuts` (GNOME 48+), `VoiceControl: true`, `GlobalHotkeys: true` when the portal answers. S01 "Global hotkeys / voice", S06 2.3 "Voice control settings".
- Mirror: `CA:VoiceBubble/*`, `M:Infrastructure/Voice/*`, `M:Views/Settings/VoiceControlSettingsPageViewController.cs`, `VoiceBrainPageViewController.cs`, `M:AppDelegate.GlobalHotKey.cs`.
- Shared: `VM:Voice/*`, voice bubble models in `Wino.Mail.Controls.Core`.
- Files: new `CG:VoiceBubble/*`, `H:Views/Voice/*`, `H:Views/Settings/VoiceControlSettingsPage.cs`, `VoiceBrainPage.cs`, `P:Native/GlobalShortcutsPortal.cs`.
- Deps: VC-01, ST-02, AC-12. Parallel: none.
- Shared touch: none.
- Verify: the portal registers the shortcut (owner confirms the GNOME prompt once); a spoken command reaches the shared pipeline (owner check).

### Package totals

| Phase | S | M | L | Packages |
| --- | --- | --- | --- | --- |
| 0 Spikes and harness | 1 | 0 | 0 | TH-01 (plus 7 spikes, SP-01 to SP-07, about half a day each, outside the repository) |
| 1 Foundation | 3 | 7 | 0 | F-01 to F-10 |
| 2 Shell | 2 | 6 | 0 | SH-01 to SH-08 |
| 3 Mail list, folders, reader | 3 | 16 | 0 | ML-01 to ML-12, RD-01 to RD-07 |
| 4 Composer and editor | 2 | 9 | 0 | ED-01 to ED-11 |
| 5 Remaining dialogs | 0 | 2 | 0 | DL-01, DL-02 |
| 6 Settings, accounts, sign-in | 7 | 19 | 3 | ST-01 to ST-14, AC-01 to AC-15 |
| 7 Calendar | 1 | 9 | 2 | CA-01 to CA-12 |
| 8 Contacts | 1 | 2 | 1 | CT-01 to CT-04 |
| 9 To Do | 0 | 4 | 1 | TD-01 to TD-05 |
| 10 Intelligence surfaces | 0 | 2 | 1 | IN-01 to IN-03 |
| 11 App lock, notifications, startup, onboarding, What's New, polish | 4 | 6 | 1 | PL-01 to PL-06, ON-01, WN-01, PO-01 to PO-03 |
| 12 Voice (needs Q6) | 0 | 0 | 2 | VC-01, VC-02 |
| **Total** | **24** | **82** | **11** | **117 packages**; 93 Fable plans (82 M + 11 L), 24 planned inline by Opus |

Without Phase 12: 115 packages (24 S, 82 M, 9 L). L packages: ST-13, AC-04, AC-05, CA-03, CA-04, CT-03, TD-02, IN-01, PL-05, VC-01, VC-02. Each L plan must name its split points (for example CA-03: grid and gutter, then tiles and all-day, then overflow and current time) so the implementing agent can stop after two builds with a working subset.

### Suggested two-lane order

Lane A works mostly in `H:` (head), lane B mostly in `PG:`, `CG:`, `EG:` and `P:`. A row is one step; the two cells of a row run at the same time. Blocked items (Q1, Q2, Q5, Q6) move without changing the rest.

| Step | Lane A | Lane B |
| --- | --- | --- |
| 0 | TH-01 | SP-01, SP-02, SP-07, then SP-03, SP-04, SP-06, then SP-05 |
| 1 | F-01 | (waits for F-01) |
| 2 | F-05 | F-02 |
| 3 | F-07 | F-06, then F-03 |
| 4 | F-08 | F-04, then F-10 |
| 5 | SH-01 | F-09 |
| 6 | SH-03 | SH-02 (control first), then ML-01 |
| 7 | SH-04, SH-05 | SH-06, SH-07 (platform and infrastructure files), then SH-08 |
| 8 | ML-02, ML-03 | RD-01, RD-03 |
| 9 | ML-04, ML-09 | RD-02, ML-05 |
| 10 | ML-11, ML-12 | RD-04, ML-08, ML-06 |
| 11 | ML-10, RD-07 | RD-05, RD-06 |
| 12 | DL-01, DL-02 | ED-01, ED-05 |
| 13 | ED-02, ED-07 | ED-03 (control first, wired into the compose page after ED-02), ED-04 |
| 14 | ED-08, ED-10 | ED-06, ED-09, ED-11 |
| 15 | ST-02 | ST-01 |
| 16 | ST-03, ST-05 | ST-04, ST-06 |
| 17 | AC-01, AC-02, AC-03 | AC-07, AC-08 |
| 18 | AC-04, AC-05, AC-06 | ST-07, ST-08, ST-09, ST-10 |
| 19 | ST-11, ST-12, ST-14, ST-13 | AC-09, AC-10, AC-11 |
| 20 | CA-01, AC-12, AC-13 | CA-02, then CA-03 (after CA-01) |
| 21 | AC-14, AC-15, CA-09, CA-10 | CA-04, CA-05 |
| 22 | CA-11, CA-06, CA-12 | CA-07, CA-08 |
| 23 | CT-01, CT-02 | TD-01, TD-02 |
| 24 | CT-03, CT-04 | TD-03, TD-04, TD-05 |
| 25 | IN-02, IN-03, ON-01, WN-01 | IN-01, PL-02, PL-03, PL-06 |
| 26 | PL-01, PL-04, PL-05 | ML-07 |
| 27 | PO-01 | PO-02, PO-03 |
| 28 | VC-01, then VC-02 (only after Q6) | |

## 4. Skipped on Linux

Genuine platform gaps. Each is hidden by its capability flag or by the Linux view omitting the row; no shared change is needed.

| Feature | Reason | What Linux does instead |
| --- | --- | --- |
| Windows widgets and live tiles (settings page, `WidgetsAndLiveTiles`) | Windows Widgets board and Start tiles do not exist | Page hidden by capability |
| Taskbar jump list | Windows shell concept | Desktop actions in the `.desktop` file (PL-04) |
| App-mode taskbar identity (`AppModeTaskbarIdentity`) | Windows taskbar concept | One application id |
| Windows Hello and biometric app lock | No freedesktop biometric prompt (fprintd is a daemon API without a system UI, and the owner decided PIN only) | PIN app lock (PL-01) |
| Microsoft Store and Apple App Store purchases, redeem, store update notifications, store review link | No store on Linux | Purchases through the Wino Account channel (AC-14); rows omitted |
| Mica and Acrylic window backdrops (`GetAvailableBackdropTypes`) | No compositor API on Wayland | Empty list; Wino themes with gradient and wallpaper stay (F-08, ST-07) |
| Tray icon and the Companion tray flyout with its settings page (`Tray`) | Stock GNOME has no tray; StatusNotifierItem needs an extension | Background keep-alive and notifications (PL-03, PL-02); page hidden by capability. Revisit with StatusNotifierItem if KDE becomes a target (Q9) |
| Dock badge (`UpdateTaskbarIconBadgeAsync`, `BadgeLaunchNavigation`, taskbar-badge rows) | No freedesktop badge standard on stock GNOME | No-op; rows hidden. Ubuntu Dock's LauncherEntry is open question Q10 |
| Platform auto-correct in the composer | No Linux text-substitution service to delegate to (IBus, Fcitx and Enchant offer none) | `autoCorrect` bridge messages ignored; the setting row omitted. Spell check works through Enchant |
| Restoring window position | Wayland clients cannot place their windows | Size and maximised state only |
| Presentation and do-not-disturb detection (`PresentationDetection`) | No freedesktop interface; the GNOME setting is GNOME-only, which `P:AGENTS.md` forbids | Reports "not presenting"; the notification server applies the user's do-not-disturb itself |
| Attachment security check (`AttachmentSecurityCheck`) | Windows attachment-scan API | Row omitted; the executable-attachment warning stays |
| Outlook legacy client accounts | The legacy Entra app is Windows-only by rule | Only `Outlook.Current` |
| Native Google and Apple sign-in SDKs for Wino Account | None on Linux | The API browser flow, as on Windows |
| Mail list row insert and remove animation, thread expand animation | `Gtk.ListView` has no item animations | Immediate update |
| Semantic-zoom group jump list | No GTK equivalent; macOS lacks it too | Group headers only |
| Print header and footer text control | WebKitGTK print has no such option | GTK print dialog defaults |

Deferred by owner decision (not a gap):

| Feature | Status |
| --- | --- |
| Push relay and live notifications (`RemoteNotifications`, relay device contracts) | Deferred. Linux keeps reporting "unsupported"; the relay rows and indicators show the shared unsupported state. Foreground and background sync cover notifications |
| Compact icon-only navigation rail (`SetPaneCompact`) | Follows the macOS decision (sidebar collapses fully). Feasible; open question Q11 |
| Voice control and global hotkeys | Feasible (PipeWire, GlobalShortcuts portal). Phase 12, waits for Q6 |
| Flatpak sandbox specifics (Secret portal, print portal, Background portal in a sandbox) | Waits for the packaging decision Q8; all APIs chosen are portal-aware |

## 5. Not on any platform

Found during the surveys as missing on Windows and macOS too. Not part of parity; do not build.

| Feature | Evidence |
| --- | --- |
| Snooze | `MailOperation` has no snooze member; no flow in either head |
| Schedule send | No `ScheduledSend` or `SendLater` in `Wino.Mail.ViewModels` |
| Find in page in the reader | Only the composer has find |
| Reader zoom | Only the reader font size preference exists |
| Dragging mail out to the desktop as `.eml` | Windows and macOS drag inside the app only |
| vCard export | The codec can encode, no command or UI in any head |
| Printing a calendar event | No print code under either Calendar folder |
| Agenda view on the desktop | Exists on the phone heads only |
| Task reminders and recurrence | Not persisted anywhere; Windows shows the rows disabled, Linux matches (TD-04) |
| Calendar pinch or Ctrl+wheel zoom | Hour height is a setting only |
| Mail search suggestions with contacts | Windows has history only (ML-11 ports that); macOS has none in Mail |

## 6. Open owner questions

| # | Question | Blocks | Default if unanswered |
| --- | --- | --- | --- |
| Q1 | Which Google OAuth client does a Linux Gmail mailbox sign-in use? The shared loopback receiver defaults to the desktop (UWP) client, which the Linux guidance forbids today; the Apple client is bundle-bound. Options: an existing client the owner names, or a new client id the owner adds | AC-10 | AC-10 stays blocked. Nothing touches the UWP client |
| Q2 | Does the Entra app `497b333d-…` have, or may it get, a redirect usable on Linux: `http://localhost` (MSAL system browser, `OAuthLoopbackRedirect: true`) or a custom scheme? | AC-09, Exchange organisation sign-in, the `OAuthLoopbackRedirect` value for generic IMAP OAuth in AC-05 | AC-09 stays blocked; generic OAuth uses the `wino://auth-complete/oauth` callback |
| Q3 | Which WinoIcons glyph set does Linux bundle: Classic (Windows), Framework7 (Apple) or Material Rounded (Android)? | F-03 (one constant) | Material Rounded, the closest to Adwaita's rounded symbolic style |
| Q4 | Settings as its own window (macOS and GNOME convention, planned) or as a mode inside the shell (Windows)? | ST-02 | Own window |
| Q5 | Which checkout URL does the `WinoAccount` purchase channel open, and does the API credit a purchase started from a Linux client? | AC-14 | AC-14 waits |
| Q6 | Are voice control and its global hotkey in scope for Linux? They are feasible but cost two L packages | VC-01, VC-02 | Not built; pages stay hidden by capability |
| Q7 | S/MIME certificate source: the per-user .NET `X509Store` (planned, same code path as Windows) or the system NSS / p11-kit stores? | PL-05 | .NET per-user store with import |
| Q8 | Packaging: Flatpak, deb, AppImage or several? It decides autostart (portal or file), font and asset install paths, the Secret portal test, and the release guide | PL-03, PL-04, PO-03 | Answered: a beta `.deb` (amd64) first; APT repository, Flatpak and arm64 later |
| Q9 | Is a StatusNotifierItem tray (and with it the Companion) wanted later for KDE or GNOME with the AppIndicator extension? | none now | No |
| Q10 | May Linux use the `com.canonical.Unity.LauncherEntry` D-Bus signal for the unread badge? Ubuntu Dock honours it, but it is not a freedesktop standard and `P:AGENTS.md` forbids desktop-specific interfaces | PL-02, ST-05 badge rows | No badge |
| Q11 | Compact icon-only rail as on Windows, or full collapse as on macOS? | SH-01 | Full collapse |
| Q12 | Sentry address and telemetry for a released Linux build | PO-03 | Answered: on in packaged Release builds, with the shared address |
| Q13 | Does Wino Intelligence (semantic index, ONNX models in `Wino.Core.ML`) ship on Linux in the first release? | AC-15, IN-01 to IN-03 | Built, entitled states verified by the owner |
| Q14 | Who verifies real pointer drag and touch gestures (mail to folder, calendar move, swipe)? Agents can only run the drop logic through Debug actions on Wayland | ML-10, ML-07, CA-07, CA-08, TD-05, CT-04 | One manual owner pass per package |

## 6a. Owner answers (2026-11-20) — binding, override section 6 defaults

| # | Answer |
| --- | --- |
| Q1 | Gmail mailbox sign-in uses the UWP client's loopback flow exactly like Android's fallback (client itself never modified). AC-10 relaxes the rule in `src/Wino.Mail.Linux/AGENTS.md` accordingly. No T7 change. |
| Q2 | Outlook: Entra app `497b333d…` with MSAL system browser and `http://localhost` redirect works (`OAuthLoopbackRedirect: true`). |
| Q3 | Material Rounded (Android set). Changed 2026-11-21: Classic (Windows set), with the same glyph per command as Windows (IC-01). |
| Q4 | Settings is its own window. |
| Q5 | Open — AC-14 waits for checkout URL. |
| Q6 | Voice: not now. VC-01/VC-02 dropped. |
| Q7 | .NET per-user X509Store. |
| Q8 | Packaging later; portal-aware APIs everywhere. |
| Q10 | No badge. |
| Q11 | Full collapse (macOS). |
| Q12 | Telemetry off. |
| Q13 | Intelligence built; owner verifies entitled states. |
| Q14 | Owner does one manual pass for drag/gesture packages; agents verify drop logic via Debug actions. |

## 7. Shared touches

Everything outside `src/Wino.Mail.Linux`, `src/Wino.Platform.Linux`, `controls/Wino.Editor.WebKitGtk` and the two new Linux control projects. All are additive. No shared C# type changes are planned.

| # | File | Change | Packages | Handoff note |
| --- | --- | --- | --- | --- |
| T1 | `src/Wino.Core.Domain/Translations/en_US/resources.json` | New English keys only where no existing key fits: keyring and startup status texts, a primary-menu label, drop-zone labels, accessibility labels. Reuse `MacOSMenu_*` and existing dialog keys first | SH-03, SH-08, ED-07, PO-01 | Localization evidence: English source only, generated `Translator` output builds, other locales untouched. Windows Debug x64 and Release x64 gate, Android Debug, macOS and iOS builds as a handoff (the generated class changes for every head) |
| T2 | `Directory.Packages.props` | New `PackageVersion` entries at 0.8.1 for Gir.Core modules used directly instead of transitively: `GirCore.Cairo-1.0`, `GirCore.Pango-1.0`, `GirCore.PangoCairo-1.0`, `GirCore.Gdk-4.0`, `GirCore.Gsk-4.0`, `GirCore.Graphene-1.0`, `GirCore.Gio-2.0`, `GirCore.GObject-2.0`; `GirCore.GObject-2.0.Integration` only if SP-01 passes | F-04, CA-02, CA-03 | Do it once, in the first package that needs it. No other platform references these packages; evidence per the Linux guide: restore and build of the Windows, Android, macOS and iOS solutions as a handoff. Published packages from nuget.org only |
| T3 | `WinoMail.Linux.slnx`, root `AGENTS.md` (guidance map and platform table rows for the two new projects), `controls/AGENTS.md` (rows for `Wino.Presentation.Linux`, `Wino.Mail.Controls.Linux`, the editor session line) | Project and documentation rows | F-01, ED-01, PO-03 | Documentation evidence: valid links. No build impact on other platforms |
| T4 | `build/Wino.Linux.targets` | Only if the two new projects are not picked up by `WinoTargetPlatform=Linux` set in their project files | F-01 | Linux-only build policy; both Linux publishes as evidence |
| T5 | `scripts/development/linux/install-desktop-entry.sh` | Register the added MIME types and desktop actions | RD-07, CT-04, PL-04 | Linux tooling; `desktop-file-validate` and `gio open` checks |
| T6 | `docs/harness/development-linux.md`, new `docs/linux-design-decisions.md`, a row in the root `AGENTS.md` reference table, `scripts/README.md` if a script is added | Guide updates: AT-SPI and Debug-action verification, Enchant, fonts, sign-in | ED-08, PO-03, AC-07 | Documentation evidence |
| T7 | `src/Wino.Core.Domain/Models/Authentication/WinoAuthenticationClientIds.cs` | Only if the owner answers Q1 with a new Linux client id: one new constant. The UWP client constant is not edited | AC-10 | Owner makes or approves this change. The file is linked into the iOS notification extension, so it stays dependency-free; Windows, Android, macOS and iOS builds as a handoff |

Explicitly not touched: `Wino.Editor.Core` (Linux copies the command mapping), `ShellMenuItemRules` (the head keeps its local copy of the grouping rule), `CalendarContextMenuLabels` and the macOS head-local shell contracts (Linux gets its own twins), `IPlatformCapabilities` (no new capability is needed), any Windows-only helper extraction.

Owner actions outside the repository: a redirect URI on the Entra app (Q2), a Google client decision (Q1), the API's handling of a Linux checkout (Q5).

## 8. Risks

| # | Risk | Effect | Mitigation |
| --- | --- | --- | --- |
| R1 | GObject subclassing fails under Native AOT, or `GirCore.GObject-2.0.Integration` has no 0.8.1 release | No real list items, no `TreeListModel`, no custom snapshot widgets | SP-01 and SP-06 first; every package names its no-subclass fallback (key lists, `Gtk.Fixed`, `DrawingArea`) |
| R2 | Calendar surface performance or delegate lifetime | CA-03 and CA-04 slip; they are the hardest packages | SP-04 with numeric limits; texture-per-unit fallback; paged navigation ships in CA-03 before the rail |
| R3 | Build cost on 6 cores and 8 GB | Agents starve each other; the AOT publish is slow | Two builds per package; never two Release builds at once; spikes outside the repository; AOT publish only in F-03, ED-01 and PO-03 |
| R4 | Wayland verification limits: no synthetic keys, pointer drags or third-party resize; the portal file dialog is not drivable | Drag, gesture and file-picker checks cannot be automated | TH-01 Debug actions (`debug-shortcut`, drop-path actions, path-injecting picker bypass in Debug), one manual owner pass (Q14) |
| R5 | Never call the Screenshot portal | It wedges `xdg-desktop-portal-gnome` and hangs every GTK app started afterwards | Screenshots only through `debug-screenshot`; recovery is `kill $(pgrep -f xdg-desktop-portal-gnome)` |
| R6 | Sign-in cannot be verified in the lab | Outlook, Gmail, Wino Account, purchases and intelligence may regress unseen | Agent checks stop at the authorization URL and the callback plumbing; owner test checklist per package; Q1 and Q2 block cleanly |
| R7 | Managed handler and GObject reference cycles | Leaks of pages, cells and windows | `WinoView.ReleaseAsync`, cell factory unbind and teardown, `WinoLiveObjects` counts in PO-02 and in each list package |
| R8 | Merge conflicts between two agents | Lost work in registries and the dialog stub file | Hotspot rules in "Conventions"; lanes split head and control projects |
| R9 | Backend parts unproven on Linux: ONNX runtime for `Wino.Core.ML`, `Wino.Providers.Exchange` Windows-only dependencies, MSAL cache on libsecret, tzdata | Features fail at run time although they build | A scratchpad run of each real service before its UI package (AC-15, AC-06, AC-09) |
| R10 | Icon font registration through fontconfig or Pango differs between distributions and in a sandbox | Tofu glyphs | SP-03 with three fallbacks; glyph check in the AOT binary; packaging decision Q8 |
| R11 | WebKitGTK behaviour: drops, clipboard images, IME, spell-check dictionaries missing, software rendering in WSL | Composer features degrade | SP-05 covers each with a fallback; dependencies documented (T6) |
| R12 | Parity drift: "libadwaita-native" drifting from the Windows layout | Rework | Each UI package's plan starts from the Windows and macOS reference files listed and compares pane proportions, density, action order and selected states in its screenshots; `docs/linux-design-decisions.md` records accepted deviations |
| R13 | Shared touches need evidence from machines this host lacks | Blocked commits | Only T1 and T2 affect other heads; batch them (one strings change per phase, one package-version change in total) and hand off the Windows, Android, macOS and iOS builds |
| R14 | Notifications and reminders fire only while the process runs | Missed reminders after quit | Background keep-alive and autostart (PL-03); documented; push relay deferred by decision |
| R15 | Secret Service locked or absent | The app does not start | Existing hard stop and message stay; no file fallback is added |
