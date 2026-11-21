# Wino.Presentation.Linux

GTK 4 and libadwaita presentation helpers for the [Linux head](../../src/Wino.Mail.Linux/AGENTS.md): the twin of `Wino.Presentation.AppKit`. It holds the binding glue (`Bindings/GtkBinder`), main-loop scheduling (`MainLoop`), signal helpers (`GtkSignals`), accessibility (`WinoA11y`), weak events (`WinoWeakEvent`) and the live-object registry for leak checks (`WinoLiveObjects`) the stylesheet (`Style/`: `WinoStyle`, `WinoCssClass`, `WinoZone` and the embedded `wino.css`) menus (`WinoMenus`, `ContextMenu`) and the WinoIcons fonts (`WinoIcons`, `WinoIcon`). List helpers and the theme come later. This file is binding for `controls/Wino.Presentation.Linux`.

## Read first

| When you | Read |
| --- | --- |
| Build, run or check the app | [Linux development](../../docs/harness/development-linux.md) |
| Change any control project | [controls/AGENTS.md](../AGENTS.md) |

## Hard stops

- The root namespace is `Wino.Presentation.Linux`, and no folder is named after a Gir.Core namespace (`Gtk`, `Gdk`, `Gsk`, `Gio`, `GLib`, `GObject`, `Adw`, `Pango`, `Cairo`, `Graphene`, `WebKit`). Inside a namespace with a child segment called `Gtk`, the expression `Gtk.Label` binds to that child and the build fails.
- References: `Wino.Core.Domain` and `Wino.Presentation.Core` only. No service, provider or ViewModel project.
- Every member runs on the GTK main thread.
- `Gtk.Internal` is used in `WinoA11y` only. It is the single accessibility entry point: Gir.Core 0.8.1 has no public `Gtk.Accessible.UpdateProperty`.
- Every signal subscription goes through `GtkSignals` and is owned by a `BindingScope` or returned as an `IDisposable`. Gir.Core 0.8.1 does not drop a handler on `-=`; `GtkSignals` connects a forwarder that reaches the handler through a box, which disposing empties.
- CSS uses libadwaita named colours only (`@accent_bg_color`, `@view_bg_color`, ...); the one fixed palette is the avatar palette from `WinoTokens`. Lengths come from the `--wino-*` variables `WinoStyle` generates. The static stylesheet loads at priority 600 (`WinoStyle.ApplicationPriority`); theme CSS uses 601 (`WinoStyle.ThemePriority`).
- Menus are `Gio.Menu` models built with `WinoMenus.Create()`; each popover gets its own `wino-menu` action group, released one main-loop turn after the popover closes (GTK activates the item after the closed signal). Context menus are attached with `ContextMenu.Attach`, never with a hand-made `Gtk.GestureClick`, so right click, long press, the Menu key and Shift+F10 behave the same everywhere.
- The icon set is named in two places only: `WinoIconSet` in the project file and `WinoIcons.SetFamily`. The fonts are registered for the process (`WinoIcons.Register`); nothing is installed into the user's font folders.
- An icon holds no handler: light, dark, high-contrast and icon-style changes are one stylesheet reload in `WinoIcons`.
- Colourful icons always draw the mono outline over the colour font (`WinoIcon`): the GPU renderers draw the colour font's foreground layer white.
- The Release gate (Native AOT, trimming, warnings as errors) applies. No reflection.

## Check your change

Build through the head, in Debug and in Release (`dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj`), then check the behaviour in the running app as the Linux guide describes.
