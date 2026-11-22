# Wino.Mail.Controls.Linux

GTK controls for the [Linux head](../../src/Wino.Mail.Linux/AGENTS.md): the twin of `Wino.Mail.Controls.AppKit`. Helpers that are not controls belong in [Wino.Presentation.Linux](../Wino.Presentation.Linux/AGENTS.md). This file is binding for `controls/Wino.Mail.Controls.Linux`.

## Read first

| When you | Read |
| --- | --- |
| Build, run or check the app | [Linux development](../../docs/harness/development-linux.md) |
| Change any control project | [controls/AGENTS.md](../AGENTS.md) |

## Hard stops

- The root namespace is `Wino.Mail.Controls.Linux`, and no folder is named after a Gir.Core namespace (`Gtk`, `Gdk`, `Gsk`, `Gio`, `GLib`, `GObject`, `Adw`, `Pango`, `Cairo`, `Graphene`, `WebKit`). Inside a namespace with a child segment called `Gtk`, the expression `Gtk.Label` binds to that child and the build fails.
- References: `Wino.Presentation.Linux` and `Wino.Mail.Controls.Core` (and through them `Wino.Core.Domain` and `Wino.Presentation.Core`) only. No service, provider or ViewModel project.
- Every member runs on the GTK main thread.
- Every signal subscription goes through `GtkSignals` and its `IDisposable` is owned by the control or its page. Gir.Core 0.8.1 keeps a handler after `-=`, so a raw `+=` whose handler captures the control keeps it and its widgets alive; check with `debug-live-objects`.
- Glyphs come from the Classic WinoIcons set of the Windows app, the same glyph the Windows app shows for the same command, menu item or tile.
- Cached Pango layouts hold an extra native reference while cached (`Calendar/PinnedLayout`); no managed delegate is handed to native code (no `Gtk.CustomLayout.New`).
- The Release gate (Native AOT, trimming, warnings as errors) applies. No reflection.

## Always

- Feature folders as in the AppKit twin: `MailList`, `Shell`, `Common`, `Settings`, `Calendar`, `Contacts`, `ToDo`, `Intelligence`, `Compose`, `Extras`. The namespace follows the folder.
- A control is a plain class that builds its widgets and exposes `Root`, unless the plan of its package says otherwise.
- A row control (the sidebar's `WinoShellRow` in the head is the pattern) is built once and switched by `Apply`; it is never rebuilt for a content change.
- A control only renders. Pages own menus, commands and navigation.
- Every interactive widget gets an accessible name through `WinoA11y.Label` (or `WinoA11y.LabelWithTooltip` for icon-only buttons); a widget hidden until hover is hidden from accessibility too, so agents and screen readers never invoke it by accident.
- `WinoContactPicture` and `WinoAccountIcon` never fetch or decode in a cell bind beyond a cache lookup: the page asks `LinuxSenderPictureProvider` (head) and sets `Picture`.
- Avatar colours come from `ContactPictureAppearance.PaletteIndex` and the `.wino-avatar-N` classes; `Adw.Avatar` is not used for people or accounts (its colours differ from the other platforms).
- `Settings/WinoDragList` reorders caller-built rows by drag, Alt+Up / Alt+Down and optional Move up / Move down buttons, and across lists that share a `WinoDragGroup`. It never moves a row: it raises `Dropped` / `Reordered`, and the owner moves its collection and calls `SetRows`.
- `Common/WinoChip` shows a text with an optional glyph or dot; colours come from `WinoTint.FillClassFor`. `MailList/WinoTileLine` puts chips (Medium, Spacious) or dots and badges (Compact) into the row's tile slots.
- To Do controls (`ToDo/`) take the shared `WinoTaskRowModel`; the round check box is a button whose owner sets `Checked` after its command ran, so a click never selects the list row. A completed title is struck through with markup, because GTK does not re-shape a laid-out label when only its CSS `text-decoration` changes.
- `Compose/WinoRecipientTokenBox` is a recipient field of `WinoRecipientChip`s and an entry; the head parses and validates through `Parse` and `Validate`, owns the recipients and builds the chip menu (`ChipMenuRequested`). Suggestions claim keys through `KeyFilter`, never a second key controller.
- `Common/WinoInfoBar` is the inline banner of pages and the card of the window info bar; its text is never markup.
- `MailList/WinoMailRow` takes a `WinoMailRowModel`; the page maps and subscribes (`MailRowCell`). Later packages extend the row through `TileSlot`, `CompactTileSlot`, `AddOverlay` and `ContentBox`, not by changing its tree.

## Check your change

Build through the head, in Debug and in Release (`dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj`), then check the control in the running app as the Linux guide describes. There is no playground.
