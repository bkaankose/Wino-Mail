# Mail-Cal

A standalone fork of [Wino Mail](https://github.com/bkaankose/Wino-Mail): a native Mail, Calendar, People, and To Do client for Windows that talks only to the mail and calendar providers you configure.

## What differs from Wino Mail

- **No external services.** The fork removes Wino Account, Wino Sync, AI and intelligence features (summaries, Daily Briefing, rewrite), Microsoft Store purchases and update checks, Sentry telemetry, Gravatar and favicon lookups, and third-party mail autodiscovery.
- **No account limit.** There is no paid add-on.
- **Calendar tray popup.** The tray icon opens a month calendar, the events of the selected day, and a quick-add form.
- **Its own name and package identity** (`hairokuma.MailCal`), so it installs next to Wino Mail. Code, projects and files use the `MailCal` name.

Network traffic goes only to your providers: IMAP/SMTP/POP3 servers, Gmail and Google Calendar APIs, Microsoft Graph, CalDAV/CardDAV servers, and their sign-in endpoints.

### OAuth client IDs

Gmail and Outlook sign-in still use the OAuth client registrations of upstream Wino Mail (`src/MailCal.Authentication/MailAuthenticatorConfiguration.cs`). For a fully independent build, register your own Azure app and Google OAuth client and replace those IDs.

## Features

- Native WinUI 3 interface with light, dark, and high-contrast support
- Outlook, Gmail, IMAP, POP3, CalDAV, CardDAV, and local data support
- Linked and merged accounts
- Offline workflows and local search
- Background synchronization and toast notifications
- WebView2-based mail reading and composition
- EML and ICS file integration
- `mailto`, `webcal`, and `webcals` protocol activation
- Encrypted local backup and restore

## Building and running

Requirements: Windows with Developer Mode, the .NET SDK from `global.json`, and [WinApp CLI](https://github.com/microsoft/WinAppCli) 0.7 or later.

```powershell
winapp run src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj --arch x64 --detach
```

See [Windows development](docs/harness/development-windows.md) for F5 debugging and build details.

## Roadmap

- Native Linux support (UI framework not decided yet: Uno Platform, Avalonia, or GTK).

## Credits and license

Mail-Cal is based on Wino Mail by Burak Kaan Köse and its contributors. Like the original, it is licensed under the GNU General Public License v3.0. See [`LICENSE.md`](LICENSE.md).
