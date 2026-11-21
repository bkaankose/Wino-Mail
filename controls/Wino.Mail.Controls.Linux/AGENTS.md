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
- Every signal subscription has a matching disconnect with the same delegate instance.
- The Release gate (Native AOT, trimming, warnings as errors) applies. No reflection.

## Always

- Feature folders as in the AppKit twin: `MailList`, `Shell`, `Common`, `Settings`, `Calendar`, `Contacts`, `ToDo`, `Intelligence`. The namespace follows the folder.
- A control is a plain class that builds its widgets and exposes `Root`, unless the plan of its package says otherwise.
- A control only renders. Pages own menus, commands and navigation.
- Every interactive widget gets an accessible name through `WinoA11y.Label` (or `WinoA11y.LabelWithTooltip` for icon-only buttons).
- `WinoContactPicture` and `WinoAccountIcon` never fetch or decode in a cell bind beyond a cache lookup: the page asks `LinuxSenderPictureProvider` (head) and sets `Picture`.
- Avatar colours come from `ContactPictureAppearance.PaletteIndex` and the `.wino-avatar-N` classes; `Adw.Avatar` is not used for people or accounts (its colours differ from the other platforms).

## Check your change

Build through the head, in Debug and in Release (`dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj`), then check the control in the running app as the Linux guide describes. There is no playground.
