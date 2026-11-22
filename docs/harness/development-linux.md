# Linux development

This is the guide for the Linux app in `src/Wino.Mail.Linux` and its solution `WinoMail.Linux.slnx` (issue #1216).
The app is a native GTK 4 and libadwaita application written in C# through Gir.Core, with WebKitGTK 6.0 for the reader. It does not use Uno, Avalonia or MAUI.
It targets GNOME on Wayland on Fedora and Ubuntu, and adapts to tiling compositors and Omarchy (see Tiling desktops and Omarchy below). It has every mode of the other desktop apps (Mail, Calendar, Contacts, To Do, Settings, account setup and sign-in, app lock, notifications, What's New); the features Linux skips are listed in the [port plan](../linux-port-plan.md#status). It is released as a beta Debian package for amd64; packaging, the update feed and telemetry are in the [Linux release guide](../releases-linux.md). Development builds have no `distribution` file, so they never check for updates or report to Sentry.

Shared rules for every platform are in [AGENTS.md](../../AGENTS.md) and the [implementation rules](implementation-rules.md). The project rules are in [src/Wino.Mail.Linux/AGENTS.md](../../src/Wino.Mail.Linux/AGENTS.md), [src/Wino.Platform.Linux/AGENTS.md](../../src/Wino.Platform.Linux/AGENTS.md), [controls/Wino.Presentation.Linux/AGENTS.md](../../controls/Wino.Presentation.Linux/AGENTS.md) and [controls/Wino.Mail.Controls.Linux/AGENTS.md](../../controls/Wino.Mail.Controls.Linux/AGENTS.md).
The port's status, the skipped features, the open handoffs to shared code and its work packages are in the [Linux port plan](../linux-port-plan.md).

## Requirements

- The .NET SDK selected by `global.json`. No workload is needed.
- GTK 4 (4.22 or later), libadwaita (1.8 or later) and WebKitGTK 6.0. Gir.Core loads the shared libraries at run time, so the runtime packages are enough to build and run; the development packages are only needed for `pkg-config` and headers.
  - Ubuntu: `sudo apt install libgtk-4-1 libadwaita-1-0 libwebkitgtk-6.0-4 libsecret-1-0 gnome-keyring libcanberra0 sound-theme-freedesktop`
  - Fedora: `sudo dnf install gtk4 libadwaita webkitgtk6.0 libsecret gnome-keyring libcanberra sound-theme-freedesktop`
  - Arch Linux and Omarchy: `sudo pacman -S gtk4 libadwaita webkitgtk-6.0 libsecret gnome-keyring libcanberra sound-theme-freedesktop`
- libcanberra plays the notification and task sounds; without it (or without a sound server) the app is silent and otherwise unaffected.
- Fonts: a CJK font (`fonts-noto-cjk`) for Chinese, Japanese and Korean text. The Wino icon fonts ship next to the executable.
- A Secret Service with an unlocked default collection (GNOME Keyring in a normal GNOME login). The app stores its protection key there and does not start without it.
- For spell checking in the composer: Enchant 2 and a dictionary for each language to check. WebKitGTK checks through Enchant; a language without a dictionary is silently not checked.
  - Ubuntu: `sudo apt install libenchant-2-2 hunspell-en-us` (or `aspell-en`; other languages: `hunspell-de-de` and so on)
  - Fedora: `sudo dnf install enchant2 hunspell-en-US`
  - Arch Linux and Omarchy: `sudo pacman -S enchant hunspell-en_us`
  - Check what Enchant finds: `enchant-lsmod-2 -list-dicts`
- For Native AOT: `clang` (or `gcc`) and `zlib` development files, as for any Native AOT build on Linux.

Last verified combination (2026-11-20, Ubuntu 26.04 under WSL2): .NET SDK 10.0.404, Gir.Core 0.8.1, GTK 4.22.4, libadwaita 1.9.1, WebKitGTK 2.52.6, libsecret 0.21.7, GNOME Shell 50.1, clang 21.1.8.

## Platform selection

The five Linux projects set `WinoTargetPlatform=Linux` in their project files, so no command passes it. The host never selects `Linux`: a plain `dotnet build` of a shared project on Linux still resolves no platform, as before.
`Directory.Build.targets` imports [build/Wino.Linux.targets](../../build/Wino.Linux.targets) for them. It sets the runtime identifiers (`linux-x64`, `linux-arm64`) and marks the assemblies as Linux-only, because the WebKitGTK bindings are annotated as unsupported on Windows and macOS.
The projects are plain `net10.0`, so the shared Release gate applies to them unchanged: the Native AOT, trimming and single-file analyzers with warnings as errors.

## Projects

| Project | Content |
| --- | --- |
| `src/Wino.Mail.Linux` | The head: composition, navigation host, dialogs, windows, and the views of every mode and of Settings |
| `src/Wino.Platform.Linux` | XDG paths, the Secret Service store, dispatcher, launcher, clipboard, notifications and sounds, autostart, sign-in brokers, S/MIME store, registration |
| `controls/Wino.Editor.WebKitGtk` | The WebKitGTK reader and editor sessions over `Wino.Editor.Core` |
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

### Lab accounts and a persistent profile

Accounts are added through the setup wizard as on the other platforms. For development, a Debug build can add one leased lab account (mail, plus calendars, contacts and tasks through the lab server's CalDAV and CardDAV service) without the wizard:

```sh
lease=$(scripts/lab/linux/lab.sh lease stalwart)     # prints the lease file path, never the password
wino-mail --lab-lease "$lease" [--lab-server stalwart]
wino-mail --lab-lease "$lease" --lab-enable-dav       # an account added before: turn on calendars, contacts and tasks
scripts/lab/linux/lab.sh renew <lease id> [ttlHours]  # before it expires (default 24 hours)
scripts/lab/linux/lab.sh release <lease id>          # when the profile is retired
scripts/lab/linux/lab.sh list                        # the local lease files
```

Keep one development profile (one `XDG_*` folder set, as in the private session above) and one lease per working copy, and reuse both across runs: pass `--lab-lease` only on the first start, when the profile has no account yet, then start without it so the app opens the existing database and synchronizes incrementally. `LabAccountBootstrap` skips an address that is already added. Renew the lease instead of leasing again, and release it only when the profile is deleted. A first-start check uses a throwaway profile that is deleted afterwards.

The lab rules are in the [root guidance](../../AGENTS.md); the manifest `~/.wino-lab/fixtures.json` must exist. A Release build ignores `--lab-lease`, but it opens data that a Debug build created in the same folders.

### Debug actions

`--debug-actions` adds application actions for checks without screen access. They exist in Debug builds only (every file is inside `#if DEBUG`; a Release `wino-mail.dll` contains no `debug-` name). The core set is in `Infrastructure/Diagnostics/DebugActions.cs`; each feature has its own `Infrastructure/Diagnostics/<Feature>DebugActions.cs`, and that file's header lists its verbs. The families:

| Area | Actions |
| --- | --- |
| Window | `debug-screenshot`, `debug-resize`, `debug-theme`, `debug-root-theme`, `debug-dump-tree`, `debug-quit` |
| Shell and navigation | `debug-navigate <Page>`, `debug-mode-state`, `debug-shell-state`, `debug-sidebar-width`, `debug-list-width`, `debug-toggle-sidebar` |
| Mail | `debug-mail-menu`, `debug-mail-drag`, the hover, swipe, selection and search verbs, `debug-compose*` |
| Calendar, Contacts, To Do | `debug-calendar`, `debug-contacts`, `debug-todo` (each takes verbs such as `details-*`, `compose-*`, `search-*`) |
| Dialogs and pickers | `debug-dialog`, `debug-account-dialog`, `debug-category-dialog`, `debug-cert-dialog`, `debug-picker-*`, `debug-reminder-editor` |
| Sign-in | `debug-signin-*`, `debug-outlook-*`, `debug-gmail-*`, `debug-embedded-signin` |
| Platform | `debug-applock`, `debug-notify`, `debug-shortcut` (keys and panes, below) |
| Desktop profile | `debug-desktop-style`, `debug-omarchy-source`, `debug-desktop-report` |
| Leaks and performance | `debug-live-objects` (live views and cells per type), `debug-perf-apply`, `debug-perf-scroll`, `debug-perf-folder-switch` |

Call them on the bus the app runs on; in the private session above that is the session's own bus, so take its address from the app's environment:

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

### UI checks by agents

Agents check UI behaviour in the running Debug app from the outside, with helpers they keep in their own scratch folder (never in the repository): a launcher that starts the app with `--debug-actions` in the private keyring session above and records the session's bus address and the app log, a `gdbus` wrapper for the actions, and a small `python3` AT-SPI client (`gi.repository.Atspi`) that lists the accessibility tree, finds widgets by name and role, clicks buttons, sets text and selects rows. Rules that keep these checks possible:

- Every interactive widget has an accessible name (`WinoA11y`), and menus are buttons with menu-item roles, so AT-SPI can find and invoke them. Hidden or hover-only buttons are not exposed while they are hidden.
- AT-SPI cannot activate a `Gtk.ListBox` row and cannot type keys, drag or resize on Wayland: navigation rows react to selection, and keys, drags and gestures have a Debug action that runs the same code path. GTK 4.22 counts section headers in AT-SPI selection indices; select rows by name.
- Run one UI session at a time (two private sessions confuse each other's AT-SPI bus), wrap long runs in `gnome-session-inhibit --inhibit idle` (a locked screen stops rendering), end with `debug-quit`, and check that no process is left on the private bus.
- Real keyboard, pointer, touch, portal file pickers and real OAuth sign-ins are owner checks, listed in the [port plan](../linux-port-plan.md#owner-manual-checklist).

### Keyboard checks

Wayland lets no agent type F6 or Escape, so `debug-shortcut` runs the whole key path with the window's real focus: the configurable shortcuts, the fixed keys of `LinuxKeymap` and the single keys, then, for a key none of them takes, the focused list's, sidebar's or search entry's own handler (Enter emits the list's `activate-item`). Each call logs one `Debug action: shortcut` line with the result, the action and the focus before and after.

```sh
act debug-shortcut "[<['list','/tmp/k.txt']>]"            # every keymap row, its accelerators and whether its action exists
act debug-shortcut "[<['focus','pane:content']>]"         # sidebar, content, detail or trailing, as F6 lands; also 'search' and 'type:Gtk.ListView'
act debug-shortcut "[<['key','F6']>]"                     # also '<Alt>Down', 'Escape', 'Return', '<Shift>Delete', 'j', 'numbersign'
act debug-shortcut "[<['pane','/tmp/p.txt']>]"            # focused pane, the panes F6 visits, focus widget, selected rows, single keys on
act debug-shortcut "[<['single','on']>]"                  # on, off or default (the desktop decides); 'on' and 'off' are stored like the Settings switch
```

Application accelerators (F1, F5, F9, Ctrl+W, Ctrl+1 to 4) are reached by activating the action itself, for example `act sync` or `act close-window`; with another window active the shell's actions log that they were ignored. Real typing into the focused window (an owner check on GNOME; on Hyprland an input tool can type plain characters) is the end-to-end check that letters still type in entries and the composer.

### Tiling desktops and Omarchy

On Hyprland, Sway, Niri, river and i3 (detected from `XDG_CURRENT_DESKTOP` and the compositors' socket variables) the Automatic window style is Tiling: no window buttons (`gtk-decoration-layout` is `:`), flat windows and thin outlines. On Omarchy the app also takes its colours from the active Omarchy theme while the Default Wino theme is selected. The rules are in the [Linux design decisions](../linux-design-decisions.md); Settings › Personalization has Window style and Follow the Omarchy theme.

```sh
act debug-desktop-style "[<'tiling'>]"        # auto, standard or tiling; not stored
act debug-omarchy-source "[<'/tmp/omfx'>]"    # treat a folder as Omarchy's current folder; '' restores detection
act debug-desktop-report "[<'/tmp/d.txt'>]"   # environment, settings, palette and why it applies, decoration layout, window sizes, theme CSS
```

`debug-desktop-report` lists every window's size against its content's minimum and every breakpoint bin inside it; a line that ends with OVERFLOW is content that a tile of that size clips, followed by the widest widgets that cause it. Check new pages at a 460 × 490 tile. On a GNOME machine a fixture stands in for Omarchy:

```sh
mkdir -p /tmp/omfx/theme
printf '%s
' 'mode = "dark"' 'accent = "#7aa2f7"' 'background = "#1a1b26"' 'dark_background = "#13141c"'   'lighter_background = "#24283b"' 'foreground = "#a9b1d6"' 'red = "#f7768e"' > /tmp/omfx/theme/colors.toml
echo fixture > /tmp/omfx/theme.name
```

The fixture applies only while the app's light or dark matches its `mode`. Rewriting `colors.toml` re-applies it within a second. Real Omarchy theme switches (`omarchy-theme-set <name>`) replace the theme folder and change the colour scheme; the app follows both without a restart.

### Desktop entry and the `wino://` scheme

```sh
scripts/development/linux/install-desktop-entry.sh [--default-mail] src/Wino.Mail.Linux/bin/Debug/net10.0/wino-mail
gio open wino://anything        # reaches the running instance; the log records the activation
scripts/development/linux/install-desktop-entry.sh --remove
```

The script installs the entry, the app icon (`icons/hicolor` next to the executable, the macOS app icon) and the AppStream metainfo (`appstreamcli validate --no-net` checks it). The entry lists `mailto:`, `message/rfc822`, `text/calendar` and `text/vcard`, so mail links, `.eml`, `.ics` and `.vcf` files can open with Wino; the script makes Wino the `mailto:` default only with `--default-mail`, and never the default for files. Its desktop actions (New Mail, New Event, Calendar, Contacts, To Do) run the same command-line options a terminal can: `--compose`, `--new-event` and `--mode mail|calendar|contacts|todo`. GApplication forwards only addresses and files to a running instance, so `Program` turns these options into head-local addresses (`wino://linux/…`) that `LinuxActivation` recognises before the shared classifier. In a private development session, open a file in the running instance with `gdbus call --session --dest app.winomail.Mail --object-path /app/winomail/Mail --method org.freedesktop.Application.Open "['file:///path/message.eml']" "{}"` on that session's bus, or run `gio open` and the options with that session's `DBUS_SESSION_BUS_ADDRESS` and `XDG_*` folders; on your own bus they would start a second instance with your keyring.

### Launch at sign-in and running in the background

Settings › General › Start minimized when you sign in writes `$XDG_CONFIG_HOME/autostart/app.winomail.Mail.desktop`, which starts the installed entry's program (else the running executable) with `--background`: the services and synchronization run and the window appears with the next activation. Inside a Flatpak sandbox the request goes through the Background portal instead. App close behavior offers Run in the background and Terminate (there is no tray icon on stock GNOME); in the background, closing the window hides it, closes pop-out windows and Settings and ends the app lock session, and Quit (Ctrl+Q, the primary menu) always ends the app.

The script also writes the scheme default to `$XDG_CONFIG_HOME/mimeapps.list`. Set both `XDG_DATA_HOME` and `XDG_CONFIG_HOME` to try it without touching your profile.
The file is installed as `app.winomail.Mail.desktop`, the application id. GNOME Shell shows notifications under the app's name and icon only when that file exists.

### Sign-in checks

A `wino://auth-complete` address that reaches the running instance completes the waiting browser sign-in and is never routed as an activation; only its path is logged. In a private session the app's own bus takes it without a desktop entry:

```sh
gdbus call --session --dest app.winomail.Mail --object-path /app/winomail/Mail --method org.freedesktop.Application.Open "['wino://auth-complete?code=test']" "{}"
```

From a browser the address needs the desktop entry above. In a Debug build, `WINO_DEBUG_CAPTURE_URIS=<file>` stops the app from opening a browser: every address it would open is appended to the file as scheme, host, path and parameter names, with values only for `redirect_uri`, `response_type`, `code_challenge_method`, `scope`, `prompt` and `access_type`. The `debug-signin-*` actions (`Infrastructure/Diagnostics/SignInDebugActions.cs`) run the brokers and the waiting dialog against it; `WINO_DEBUG_SIGNIN_TIMEOUT_SECONDS` shortens their time limit. Agents stop at the authorization address and the callback plumbing; the real sign-ins with Outlook, Gmail and OAuth providers are owner checks.

Outlook signs in with MSAL's own `http://localhost:<port>` listener; `debug-outlook-signin 'hint'`, `debug-outlook-cancel` and `debug-outlook-cache` drive it. Owner checks with a real Outlook.com and a work account: add the account in the browser and let mail, calendar, contacts and To Do synchronise; quit and start again without a browser prompt; Reconnect contacts and tasks in Account details; remove the account (`debug-outlook-cache` shows one account less) and add it again; revoke the app's access at account.microsoft.com and fix the account from its attention row.

Gmail uses the shared loopback receiver (`http://127.0.0.1:<port>/authorize/`, PKCE) with the client `IAuthenticatorConfig` supplies, as Android's fallback; `debug-gmail-receiver`, `debug-gmail-signin` (a request without a client id, for the listener and the waiting dialog) and `debug-gmail-cancel` drive it. Owner checks with a real Gmail account: add it in the browser, the Wino result page shows and the app adds the account with labels, mail, calendar, contacts and tasks; restart without a browser prompt; remove and add it again; revoke access in the Google account and fix it from the attention row; confirm the Windows app with the same Google account is unaffected. A sandboxed package later needs network access for the loopback listener.

## System services

| Need | How | Notes |
| --- | --- | --- |
| Protection key | Secret Service through libsecret (`LibsecretSecureItemStore`) | One item, schema `app.winomail.Mail.SecureItem`. No file fallback. Credentials themselves are AES-GCM files below `$XDG_DATA_HOME/wino-mail/secure`. |
| Notifications | `GNotification`, which GLib sends over org.freedesktop.Notifications, or the notification portal in a sandbox | New mail only |
| Opening links and files | `Gtk.UriLauncher`, `Gtk.FileLauncher` | xdg-desktop-portal (OpenURI) in a sandbox |
| Dark and light | `Adw.StyleManager` | Follows the Settings portal (`org.freedesktop.appearance color-scheme`), live. `GtkThemeService` applies the user's choice through `Adw.StyleManager.ColorScheme`, and the accent and the Wino theme backdrop through one CSS provider at priority 601; theme wallpapers are copied to `Themes/` next to the executable |
| Activation | `GApplication` with `HANDLES_OPEN`, one instance per session | `x-scheme-handler/wino` and `mailto`, `.eml`, `.ics`, `.vcf` in the `.desktop` file; desktop actions as head-local `wino://linux/` addresses |
| Launch at sign-in | freedesktop autostart entry; the Background portal in a sandbox | `--background` starts without a window |
| S/MIME certificates | The per-user .NET certificate store, in stores named `wino-mail-personal` and `wino-mail-recipient` (Linux has no certificate friendly names to mark Wino's own) | **Private keys are protected by file permissions only:** .NET keeps each store as files under `~/.dotnet/corefx/cryptography/x509stores` (mode 0600), not in the keyring, so anyone who can read the user's files can read the keys. Each signing or decryption copies the certificates into an in-memory MimeKit context; the system CA bundle (the machine Root store) is the trust anchor set. Start a development run with `HOME` set to a scratch folder to keep test certificates out of your real store |
| Notification sounds | Freedesktop sound-theme events through libcanberra (`libcanberra.so.0`, non-variadic calls only) | Played when a notification is shown and for a completed task; without the library or a sound server they are silent. GNOME's do-not-disturb is not readable over a freedesktop interface, so it does not silence them |
| Printing and PDF | `WebKit.PrintOperation`: the GTK print dialog; Save as PDF prints without a dialog to GTK's "Print to File" printer | Part of GTK 4, no CUPS needed. The reader sets its own `LinuxMailPrintPresenter` |

## Trimming and Native AOT

Both work with Gir.Core 0.8.1 and WebKitGTK, checked by running the published app, not only by analysis:

```sh
dotnet publish src/Wino.Mail.Linux/Wino.Mail.Linux.csproj -c Release -r linux-x64 -p:WinoLinuxPublish=Trimmed -o <folder>
dotnet publish src/Wino.Mail.Linux/Wino.Mail.Linux.csproj -c Release -r linux-x64 -p:WinoLinuxPublish=Aot -o <folder>
```

Use `WinoLinuxPublish`, not `-p:PublishTrimmed` or `-p:PublishAot`: a global property reaches the `netstandard2.0` source generator project and fails the build (`NETSDK1124`).
Never overlap a publish with another Release build or publish on the same machine (memory). The publish warnings that remain come from backend dependencies (Ical.Net, the EWS library, RecyclableMemoryStream), not from the GTK bindings or the Linux projects. The Native AOT executable is about 95 MB (its symbols are in a separate `wino-mail.dbg`), plus the native libraries of SkiaSharp, SQLite and ONNX Runtime.

## Verification

| Change | Required evidence |
| --- | --- |
| Any Linux project | Debug build and Release build with 0 warnings |
| Views, navigation, the reader or editor session | Checks in a GNOME Wayland session with the leased lab account of a persistent profile: the changed page in light and dark, at a wide and a narrow width, `debug-live-objects` back at its baseline after leaving the page, and the AT-SPI names of new widgets |
| Secret Service, paths | A first start in an empty data folder, and a second start that reads the same data |
| Desktop entry, activation, notifications | `desktop-file-validate`, `gio open wino://…` against a running instance, and a delivered lab message (`deliver` action of the control API) |
| Project files, `build/Wino.Linux.targets`, publish settings | Both publishes above, and a run of each published app |
| `scripts/release/linux`, the package layout or the update feed | `build-linux-release.sh --non-interactive --skip-translation` into a scratch output root, `dpkg-deb -I`/`-c` of the result, and `publish-linux-release.sh --dry-run` on it; never the upload |
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

## Known limitations

- Beta only: one unsigned amd64 `.deb` with an in-app update check, no APT repository, no Flatpak and no arm64 build yet ([Linux release guide](../releases-linux.md#later)); all APIs chosen are portal-aware.
- GNOME on Wayland is the reference desktop; tiling compositors and Omarchy get the desktop profile above, other desktops behave as GNOME. Wayland clients cannot place windows, so only size and maximised state are restored. The reader and composer web views do not take Omarchy's colours. No tray icon, no dock badge, no do-not-disturb detection.
- Private keys of S/MIME certificates are protected by file permissions only (see System services).
- Linux skips the features listed under "Skipped on Linux" in the [port plan](../linux-port-plan.md#4-skipped-on-linux); bugs found on Linux that live in shared code are open handoffs there.
- Calendar glide in Week view drops frames on software rendering (no GPU); the mail list drops frames while a very large synchronization runs.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| "The system keyring is not available" at start | No Secret Service, or no unlocked default collection. Use the private session above, or unlock the keyring. |
| `DllNotFoundException: Unable to load shared library 'WebKit'` | The Gir.Core module was not initialized. Call `WebKit.Module.Initialize()` before the first WebKit type is used. |
| `CA1416` on WebKit members | The project lacks `WinoTargetPlatform=Linux`, so `build/Wino.Linux.targets` did not mark the assembly as Linux-only. |
| `NETSDK1124` on publish | `PublishTrimmed` or `PublishAot` was passed on the command line. Use `-p:WinoLinuxPublish=…`. |
| Squares instead of Chinese, Japanese or Korean text | No CJK font is installed (`fonts-noto-cjk` on Ubuntu). |
| "Wino Mail could not start" after a crash, with a `Wino210.db-journal` next to the database | A write was interrupted. `DatabaseJournalRecovery` rolls the journal back before startup; if it still fails, open the database once read-write (`sqlite3 Wino210.db 'pragma integrity_check'`). |
| An error appears in the log file only after a while | The log file is buffered. Wait a few seconds or close the app. |
| Dozens of `gvfsd`, `goa-daemon` or `evolution-*` processes after headless runs | A private session was killed instead of ended, so the helpers its bus started live on. Let the session's last command exit, or stop the processes whose `DBUS_SESSION_BUS_ADDRESS` points at `/tmp/dbus-*`. |
| `libEGL` and `MESA` warnings under WSL or headless | No GPU; GTK falls back to software rendering. Harmless. |
| Squares or wrong symbols instead of Wino icons | `Fonts/WinoIcons*.ttf` (the Classic set, linked from `src/Wino.Mail.WinUI/Assets`) is missing next to the executable, or Pango is older than 1.56 and fontconfig rejected the files. The start log has a line `Wino icon fonts`. |
