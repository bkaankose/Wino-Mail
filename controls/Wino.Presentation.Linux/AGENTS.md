# Wino.Presentation.Linux

GTK 4 and libadwaita presentation helpers for the [Linux head](../../src/Wino.Mail.Linux/AGENTS.md): the twin of `Wino.Presentation.AppKit`. It holds the binding glue (`Bindings/GtkBinder`), main-loop scheduling (`MainLoop`), signal helpers (`GtkSignals`), accessibility (`WinoA11y`), weak events (`WinoWeakEvent`) and the live-object registry for leak checks (`WinoLiveObjects`). Style, icons, list and menu helpers and the theme come later. This file is binding for `controls/Wino.Presentation.Linux`.

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
- Every signal subscription has a matching disconnect with the same delegate instance, owned by a `BindingScope` or returned as an `IDisposable`.
- The Release gate (Native AOT, trimming, warnings as errors) applies. No reflection.

## Check your change

Build through the head, in Debug and in Release (`dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj`), then check the behaviour in the running app as the Linux guide describes.
