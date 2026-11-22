# Wino.Presentation.Linux

GTK 4 and libadwaita presentation helpers for the [Linux head](../../src/Wino.Mail.Linux/AGENTS.md): the twin of `Wino.Presentation.AppKit`. It holds the binding glue (`Bindings/GtkBinder`), main-loop scheduling (`MainLoop`), signal helpers (`GtkSignals`), accessibility (`WinoA11y`), weak events (`WinoWeakEvent`) and the live-object registry for leak checks (`WinoLiveObjects`) the stylesheet (`Style/`: `WinoStyle`, `WinoCssClass`, `WinoZone`, `WinoTint` for arbitrary text colours and `FillClassFor` background and text pairs and the embedded `wino.css`) menus (`WinoMenus`, `WinoMenuPopover`, `ContextMenu`) and the WinoIcons fonts (`WinoIcons`, `WinoIcon`) and the list stack (`Lists/`: `ObservableListModel`, `WinoCellFactory`, `WinoHeaderFactory`, `ListSections`, `ListViews`). This file is binding for `controls/Wino.Presentation.Linux`.

## Read first

| When you | Read |
| --- | --- |
| Build, run or check the app | [Linux development](../../docs/harness/development-linux.md) |
| Change any control project | [controls/AGENTS.md](../AGENTS.md) |

## Hard stops

- The root namespace is `Wino.Presentation.Linux`, and no folder is named after a Gir.Core namespace (`Gtk`, `Gdk`, `Gsk`, `Gio`, `GLib`, `GObject`, `Adw`, `Pango`, `Cairo`, `Graphene`, `WebKit`). Inside a namespace with a child segment called `Gtk`, the expression `Gtk.Label` binds to that child and the build fails.
- References: `Wino.Core.Domain` and `Wino.Presentation.Core` only. No service, provider or ViewModel project.
- Every member runs on the GTK main thread.
- `Gdk.Texture` objects are created on the main thread (`Textures`); files are read off it (`Textures.ReadAsync`).
- `Gtk.Internal` is used in `WinoA11y` only. It is the single accessibility entry point: Gir.Core 0.8.1 has no public `Gtk.Accessible.UpdateProperty`.
- Every signal subscription goes through `GtkSignals` and is owned by a `BindingScope` or returned as an `IDisposable`. Gir.Core 0.8.1 does not drop a handler on `-=`; `GtkSignals` connects a forwarder that reaches the handler through a box, which disposing empties.
- CSS uses libadwaita named colours only (`@accent_bg_color`, `@view_bg_color`, ...); the one fixed palette is the avatar palette from `WinoTokens`. Lengths come from the `--wino-*` variables `WinoStyle` generates. The static stylesheet loads at priority 600 (`WinoStyle.ApplicationPriority`); theme CSS uses 601 (`WinoStyle.ThemePriority`). `ThemeCss` is the one place that redefines libadwaita's named colours (the accent and a desktop palette such as Omarchy's, in both the `--name` and `@define-color` spellings) and emits the flat tiling look.
- Menus are built with `WinoMenus.Create()` and shown with `ContextMenu.Show` / `ContextMenu.Attach` in a `WinoMenuPopover` (rows are buttons with menu-item roles, so assistive technology names and clicks them); never `Gtk.PopoverMenu`, `Gio.Menu` or a hand-made `Gtk.GestureClick`, so right click, long press, the Menu key and Shift+F10 behave the same everywhere. An item runs one main-loop turn after its menu closed. Glyphs are `WinoMenuIcon`s (a `WinoIconGlyph` converts); destructive items pass `destructive: true` and draw in `@destructive_color`.
- Linux draws the Classic set of the Windows app (`src/Wino.Mail.WinUI/Assets/WinoIcons*.ttf`, families `WinoIcons`, `WinoIconsColorLight`, `WinoIconsColorDark`). The set is named in two places only: `WinoIconFontDirectory` in the project file and `WinoIcons.SetFamily`. A glyph for a command, menu item or tile is the one the Windows app shows for the same thing. The fonts are registered for the process (`WinoIcons.Register`); nothing is installed into the user's font folders.
- An icon holds no handler: light, dark, high-contrast and icon-style changes are one stylesheet reload in `WinoIcons`.
- Colourful icons always draw the mono outline over the colour font (`WinoIcon`): the GPU renderers draw the colour font's foreground layer white.
- Gir.Core wraps a GObject with a toggle reference, so native code that takes and drops a reference calls back into managed code each time. An object handed to native code many times per frame (a cached `Pango.Layout` drawn with `pango_cairo_show_layout`) holds one extra `g_object_ref` while it is cached (`PinnedLayout` in the calendar is the pattern) and releases it on dispose.
- Never pass a managed delegate that native code keeps (Gir.Core's `Gtk.CustomLayout.New` included): it is collected and the app crashes. Use `[UnmanagedCallersOnly]` function pointers.
- `ManagedItem` is the only GObject subclass; it is not sealed and has no constructor (generator rules). Do not add item subclasses: put the object into `Value`. `WinoPresentation.Initialize()` registers it and runs before the application object is created.
- A `WinoCellFactory` and an `ObservableListModel` are disposed by their owner; an undisposed factory leaks its cells.
- With `ListSections`, positions are positions of the sorted model: convert them with `ItemAt`, never with the model's indexer.
- The Release gate (Native AOT, trimming, warnings as errors) applies. No reflection.

## Check your change

Build through the head, in Debug and in Release (`dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj`), then check the behaviour in the running app as the Linux guide describes.
