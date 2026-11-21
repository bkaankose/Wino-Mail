# Linux development

This is the guide for the Linux proof of concept in `src/Wino.Mail.Linux` and its solution `WinoMail.Linux.slnx` (issue #1216).
The app is a native GTK 4 and libadwaita application written in C# through Gir.Core, with WebKitGTK 6.0 for the reader. It does not use Uno, Avalonia or MAUI.
It targets GNOME on Wayland on Fedora and Ubuntu. It shows the shell, the folder list, the mail list and the reader for an IMAP account; everything else is missing, and it is not released.

Shared rules for every platform are in [AGENTS.md](../../AGENTS.md) and the [implementation rules](implementation-rules.md). The project rules are in [src/Wino.Mail.Linux/AGENTS.md](../../src/Wino.Mail.Linux/AGENTS.md) and [src/Wino.Platform.Linux/AGENTS.md](../../src/Wino.Platform.Linux/AGENTS.md).
The scope of the port, its work packages and their order are in the [Linux port plan](../linux-port-plan.md).

## Requirements

- The .NET SDK selected by `global.json`. No workload is needed.
- GTK 4, libadwaita and WebKitGTK 6.0. Gir.Core loads the shared libraries at run time, so the runtime packages are enough to build and run; the development packages are only needed for `pkg-config` and headers.
  - Ubuntu: `sudo apt install libgtk-4-1 libadwaita-1-0 libwebkitgtk-6.0-4 libsecret-1-0 gnome-keyring`
  - Fedora: `sudo dnf install gtk4 libadwaita webkitgtk6.0 libsecret gnome-keyring`
- A Secret Service with an unlocked default collection (GNOME Keyring in a normal GNOME login). The app stores its protection key there and does not start without it.
- For Native AOT: `clang` (or `gcc`) and `zlib` development files, as for any Native AOT build on Linux.

Last verified combination (2026-11-20, Ubuntu 26.04 under WSL2): .NET SDK 10.0.404, Gir.Core 0.8.1, GTK 4.22.4, libadwaita 1.9.1, WebKitGTK 2.52.6, libsecret 0.21.7, GNOME Shell 50.1, clang 21.1.8.

## Platform selection

The five Linux projects set `WinoTargetPlatform=Linux` in their project files, so no command passes it. The host never selects `Linux`: a plain `dotnet build` of a shared project on Linux still resolves no platform, as before.
`Directory.Build.targets` imports [build/Wino.Linux.targets](../../build/Wino.Linux.targets) for them. It sets the runtime identifiers (`linux-x64`, `linux-arm64`) and marks the assemblies as Linux-only, because the WebKitGTK bindings are annotated as unsupported on Windows and macOS.
The projects are plain `net10.0`, so the shared Release gate applies to them unchanged: the Native AOT, trimming and single-file analyzers with warnings as errors.

## Projects

| Project | Content |
| --- | --- |
| `src/Wino.Mail.Linux` | The head: composition, navigation host, dialogs, and the shell, mail list and reader views |
| `src/Wino.Platform.Linux` | XDG paths, the Secret Service store, dispatcher, launcher, clipboard, notifications, registration |
| `controls/Wino.Editor.WebKitGtk` | The WebKitGTK reader session over `Wino.Editor.Core` |
| `controls/Wino.Presentation.Linux` | GTK presentation helpers: binding glue, main-loop scheduling, accessibility, weak events, live objects, stylesheet, menus, the WinoIcons fonts |
| `controls/Wino.Mail.Controls.Linux` | GTK controls, the twin of `Wino.Mail.Controls.AppKit` |

## Build and run

```sh
dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj                 # Debug
dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj -c Release      # the gate; must end with 0 warnings
src/Wino.Mail.Linux/bin/Debug/net10.0/wino-mail
```

In a GNOME session the login keyring is already unlocked and the command above is all that is needed. Data goes to `$XDG_DATA_HOME/wino-mail` (`~/.local/share/wino-mail`) and temporary files to `$XDG_CACHE_HOME/wino-mail`.

### A development session with its own keyring

WSL and headless sessions have no login keyring, and a development run should not write to your real keyring or data anyway. Run the app in a private D-Bus session with its own unlocked keyring and its own XDG folders:

```sh
state=~/.local/state/wino-mail-dev
export XDG_DATA_HOME=$state/data XDG_CACHE_HOME=$state/cache XDG_CONFIG_HOME=$state/config
mkdir -p "$XDG_DATA_HOME" "$XDG_CACHE_HOME" "$XDG_CONFIG_HOME"
dbus-run-session -- bash -c '
  eval "$(printf %s wino-development | gnome-keyring-daemon --unlock --components=secrets)"
  export GNOME_KEYRING_CONTROL
  exec src/Wino.Mail.Linux/bin/Debug/net10.0/wino-mail "$@"' wino-mail --lab-lease "$lease"
```

The keyring password in that command protects throwaway development data only. Under WSLg the window appears on the Windows desktop.

### Debug-only commands

Account setup has no screen yet. A Debug build adds one leased lab account on its first start, with mail and with calendars, contacts and tasks through the lab server's CalDAV and CardDAV service:

```sh
lease=$(scripts/lab/linux/lab.sh lease stalwart)     # prints the lease file path
wino-mail --lab-lease "$lease" [--lab-server stalwart]
wino-mail --lab-lease "$lease" --lab-enable-dav       # an account added before: turn on calendars, contacts and tasks
scripts/lab/linux/lab.sh renew <lease id> [ttlHours]  # before it expires (default 24 hours)
scripts/lab/linux/lab.sh release <lease id>          # when done
```

The lab rules are in the [root guidance](../../AGENTS.md); the manifest `~/.wino-lab/fixtures.json` must exist. A Release build ignores `--lab-lease`, but it opens data that a Debug build created in the same folders.

`--debug-actions` adds application actions for checks without screen access (`Infrastructure/Diagnostics/DebugActions.cs`, one file per package, Debug builds only). Call them on the bus the app runs on; in the private session below that is the session's own bus, so take its address from the app's environment:

```sh
export DBUS_SESSION_BUS_ADDRESS=$(tr '\0' '\n' < /proc/$(pgrep -x wino-mail)/environ | sed -n 's/^DBUS_SESSION_BUS_ADDRESS=//p')
act() { gdbus call --session --dest app.winomail.Mail --object-path /app/winomail/Mail --method org.freedesktop.Application.ActivateAction "$1" "${2:-[]}" "{}"; }
act debug-screenshot "[<['/tmp/w.png']>]"                 # the window's client area; add a second item to capture the widget with that accessible label
act debug-resize "[<(1008, 700)>]"                        # the compositor still limits it to the work area
act debug-theme "[<'dark'>]"                              # light, dark or system
act debug-dump-tree "[<'/tmp/tree.txt'>]"                 # type, role, label, CSS classes, bounds and state of every widget
act debug-quit
```

Results and failures go to the app log. Never take pictures through the xdg-desktop-portal Screenshot method or `org.gnome.Shell.Screenshot` on a desktop session: the portal call blocks and leaves `xdg-desktop-portal-gnome` hung, so every GTK app started afterwards hangs too (recover with `kill $(pgrep -f xdg-desktop-portal-gnome)`). AT-SPI (`python3` with `gi.repository.Atspi`) reads the tree and clicks buttons, but cannot resize a window or type keys on Wayland, and a sidebar `Gtk.ListBoxRow` has no AT-SPI action: selecting it through `Atspi.Selection` does not emit `row-activated`.

### Desktop entry and the `wino://` scheme

```sh
scripts/development/linux/install-desktop-entry.sh src/Wino.Mail.Linux/bin/Debug/net10.0/wino-mail
gio open wino://anything        # reaches the running instance; the log records the activation
scripts/development/linux/install-desktop-entry.sh --remove
```

The script also writes the scheme default to `$XDG_CONFIG_HOME/mimeapps.list`. Set both `XDG_DATA_HOME` and `XDG_CONFIG_HOME` to try it without touching your profile.
The file is installed as `app.winomail.Mail.desktop`, the application id. GNOME Shell shows notifications under the app's name and icon only when that file exists.

## System services

| Need | How | Notes |
| --- | --- | --- |
| Protection key | Secret Service through libsecret (`LibsecretSecureItemStore`) | One item, schema `app.winomail.Mail.SecureItem`. No file fallback. Credentials themselves are AES-GCM files below `$XDG_DATA_HOME/wino-mail/secure`. |
| Notifications | `GNotification`, which GLib sends over org.freedesktop.Notifications, or the notification portal in a sandbox | New mail only |
| Opening links and files | `Gtk.UriLauncher`, `Gtk.FileLauncher` | xdg-desktop-portal (OpenURI) in a sandbox |
| Dark and light | `Adw.StyleManager` | Follows the Settings portal (`org.freedesktop.appearance color-scheme`), live. `GtkThemeService` applies the user's choice through `Adw.StyleManager.ColorScheme`, and the accent and the Wino theme backdrop through one CSS provider at priority 601; theme wallpapers are copied to `Themes/` next to the executable |
| Activation | `GApplication` with `HANDLES_OPEN`, one instance per session | `x-scheme-handler/wino` in the `.desktop` file |

## Trimming and Native AOT

Both work with Gir.Core 0.8.1 and WebKitGTK, checked by running the published app, not only by analysis:

```sh
dotnet publish src/Wino.Mail.Linux/Wino.Mail.Linux.csproj -c Release -r linux-x64 -p:WinoLinuxPublish=Trimmed -o <folder>
dotnet publish src/Wino.Mail.Linux/Wino.Mail.Linux.csproj -c Release -r linux-x64 -p:WinoLinuxPublish=Aot -o <folder>
```

Use `WinoLinuxPublish`, not `-p:PublishTrimmed` or `-p:PublishAot`: a global property reaches the `netstandard2.0` source generator project and fails the build (`NETSDK1124`).
The publish warnings that remain come from backend dependencies (Ical.Net, the EWS library, RecyclableMemoryStream), not from the GTK bindings or the Linux projects. The Native AOT executable is about 95 MB (its symbols are in a separate `wino-mail.dbg`), plus the native libraries of SkiaSharp, SQLite and ONNX Runtime.

## Verification

| Change | Required evidence |
| --- | --- |
| Any Linux project | Debug build and Release build with 0 warnings |
| Views, navigation, the reader session | Manual checks in a GNOME Wayland session with a leased lab account: folders, list, opening plain and HTML messages, dark and light |
| Secret Service, paths | A first start in an empty data folder, and a second start that reads the same data |
| Desktop entry, activation, notifications | `desktop-file-validate`, `gio open wino://…` against a running instance, and a delivered lab message (`deliver` action of the control API) |
| Project files, `build/Wino.Linux.targets`, publish settings | Both publishes above, and a run of each published app |
| `Directory.Build.*`, `Directory.Packages.props` | Also the shared-code evidence of the root guidance (Windows, Android, macOS, iOS) |

### Headless GNOME session

A real GNOME Shell runs without a display, which is how screenshots are taken on a machine with no GNOME desktop (WSL, CI). Inside the private session above:

```sh
gnome-shell --headless --no-x11 --wayland-display wino-headless --virtual-monitor 1440x900 &
WAYLAND_DISPLAY=wino-headless GDK_BACKEND=wayland src/Wino.Mail.Linux/bin/Debug/net10.0/wino-mail &
```

- The shell starts in the overview. Leave it by setting the `OverviewActive` property of `org.gnome.Shell` to false over D-Bus.
- `org.gnome.Shell.Screenshot.Screenshot` takes the picture. The shell accepts the call only from the owner of `org.gnome.SettingsDaemon.MediaKeys` or of the GNOME portal name; in a private session nothing owns the first, so the calling script requests it.
- `org.gnome.Mutter.RemoteDesktop` injects pointer and keyboard input (`NotifyPointerMotionRelative`, `NotifyPointerButton`, `NotifyKeyboardKeysym`). Turn hot corners off first (`gsettings set org.gnome.desktop.interface enable-hot-corners false`), or a pointer parked in the top-left corner opens the overview.
- `gsettings set org.gnome.desktop.interface color-scheme prefer-dark` switches the session to dark.

Do not add such a script to the repository; each check is run ad hoc and reported with its screenshots.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| "The system keyring is not available" at start | No Secret Service, or no unlocked default collection. Use the private session above, or unlock the keyring. |
| `DllNotFoundException: Unable to load shared library 'WebKit'` | The Gir.Core module was not initialized. Call `WebKit.Module.Initialize()` before the first WebKit type is used. |
| `CA1416` on WebKit members | The project lacks `WinoTargetPlatform=Linux`, so `build/Wino.Linux.targets` did not mark the assembly as Linux-only. |
| `NETSDK1124` on publish | `PublishTrimmed` or `PublishAot` was passed on the command line. Use `-p:WinoLinuxPublish=…`. |
| Squares instead of Chinese, Japanese or Korean text | No CJK font is installed (`fonts-noto-cjk` on Ubuntu). |
| An error appears in the log file only after a while | The log file is buffered. Wait a few seconds or close the app. |
| Dozens of `gvfsd`, `goa-daemon` or `evolution-*` processes after headless runs | A private session was killed instead of ended, so the helpers its bus started live on. Let the session's last command exit, or stop the processes whose `DBUS_SESSION_BUS_ADDRESS` points at `/tmp/dbus-*`. |
| `libEGL` and `MESA` warnings under WSL or headless | No GPU; GTK falls back to software rendering. Harmless. |
| Squares or wrong symbols instead of Wino icons | `Fonts/WinoIcons*.ttf` is missing next to the executable, or Pango is older than 1.56 and fontconfig rejected the files. The start log has a line `Wino icon fonts`. |
