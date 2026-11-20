# Wino.Platform.Linux

Linux platform services for the [Linux proof of concept](../Wino.Mail.Linux/AGENTS.md), over freedesktop standards only: XDG base directories, the Secret Service through libsecret, desktop notifications, and xdg-desktop-portal through GTK. It has no views and no navigation.

## Read first

| When you | Read |
| --- | --- |
| Build or run, or need a keyring in a development session | [Linux development](../../docs/harness/development-linux.md) |
| Add a contract a head must supply | [Wino.Hosting](../Wino.Hosting/AGENTS.md) and `src/Wino.Platform.Android/AndroidPlatformRegistration.cs` |

## Hard stops

- **The protection key lives in the Secret Service and nowhere else.** `LibsecretSecureItemStore` has no file fallback. When the service is missing or locked, `LinuxPlatformBootstrap.Create` throws `SecretServiceException` and the app stops with a message. Never add an unprotected store, and never use `FileProtectionKeyProvider`, which is for `Wino.Hosting` only.
- **Item names are permanent.** The schema `app.winomail.Mail.SecureItem`, its attributes `application`, `scope` and `account`, and the folder names in `LinuxPaths` name existing data. Changing one orphans every installation.
- **Freedesktop standards only.** No GNOME-only or KDE-only D-Bus interface. Open addresses and files through `Gtk.UriLauncher` and `Gtk.FileLauncher`, and pick files through `Gtk.FileDialog`: they use the portals inside a sandbox.
- **No sign-in here yet.** The sign-in contracts throw `NotSupportedException`. Do not add an OAuth client id; the rules are in the root guidance.
- Telemetry stays off: `LinuxApplicationConfiguration` returns an empty Sentry address until the head is released.

## Always

- Reuse a portable implementation from `Wino.Services` or `Wino.Authentication` before writing one here. `AddPortablePlatformServices` supplies the ones every non-Windows head shares.
- Call libsecret through `LibraryImport` and only through its non-variadic functions (`secret_password_lookupv_sync`, `secret_password_storev_sync`, `secret_password_clearv_sync`). Variadic C functions cannot be called safely from .NET.
- Create `GtkDispatcher` on the GTK main thread. Work from that thread runs inline; other threads queue on the main context.
- When shared code gains a contract a head must supply, register it in `LinuxPlatformRegistration.cs` in the same change.

## Check your change

Build through the head (`dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj`), in Debug and in Release. Then start the app in a session with an unlocked keyring and confirm the changed service in the running app; the Linux guide has the commands. To check the keyring item itself:

```sh
secret-tool search application app.winomail.Mail
```

It prints the item's attributes and its secret, so do not paste the output anywhere.
