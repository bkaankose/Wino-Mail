# Wino.Platform.Linux

Linux platform services for the [Linux app](../Wino.Mail.Linux/AGENTS.md), over freedesktop standards only: XDG base directories, the Secret Service through libsecret, desktop notifications, and xdg-desktop-portal through GTK. It has no views and no navigation.

## Read first

| When you | Read |
| --- | --- |
| Build or run, or need a keyring in a development session | [Linux development](../../docs/harness/development-linux.md) |
| Add a contract a head must supply | [Wino.Hosting](../Wino.Hosting/AGENTS.md) and `src/Wino.Platform.Android/AndroidPlatformRegistration.cs` |

## Hard stops

- **The protection key lives in the Secret Service and nowhere else.** `LibsecretSecureItemStore` has no file fallback. When the service is missing or locked, `LinuxPlatformBootstrap.Create` throws `SecretServiceException` and the app stops with a message. Never add an unprotected store, and never use `FileProtectionKeyProvider`, which is for `Wino.Hosting` only.
- **Item names are permanent.** The schema `app.winomail.Mail.SecureItem`, its attributes `application`, `scope` and `account`, and the folder names in `LinuxPaths` name existing data. Changing one orphans every installation.
- **Freedesktop standards only.** No GNOME-only or KDE-only D-Bus interface. Reading a desktop's environment variables and state files is allowed (`Desktop/LinuxDesktopEnvironment`, `Desktop/OmarchyPalette`); its D-Bus interfaces are still not. Open addresses and files through `Gtk.UriLauncher` and `Gtk.FileLauncher`, and pick files through `Gtk.FileDialog`: they use the portals inside a sandbox.
- **Sign-in:** the sign-in contracts are real: browser callbacks (`LinuxExternalAuthCallbacks`), the OAuth and Wino Account brokers, the Outlook host (`LinuxOutlookAuthenticationHost`); the head supplies the embedded browser (`GtkEmbeddedBrowserAuthBroker`), and Gmail uses the shared loopback receiver. Do not add an OAuth client id; the rules are in the root guidance.
- The MSAL token cache item `msal-cache` / `outlook-user-cache` is permanent: renaming it signs every Outlook account out.
- Telemetry is on only in a packaged Release build: `LinuxPlatformRegistration` passes the shared Sentry address when `LinuxDistribution` finds the package's `distribution` file. Debug builds and plain publishes keep it empty, so they never report lab data.

## Always

- Reuse a portable implementation from `Wino.Services` or `Wino.Authentication` before writing one here. `AddPortablePlatformServices` supplies the ones every non-Windows head shares.
- Call libsecret through `LibraryImport` and only through its non-variadic functions (`secret_password_lookupv_sync`, `secret_password_storev_sync`, `secret_password_clearv_sync`). Variadic C functions cannot be called safely from .NET. libcanberra (`Native/Canberra.cs`) follows the same rule: `ca_context_play_full` with a property list, a native callback as an `[UnmanagedCallersOnly]` function pointer, and a missing library means silence, not an error.
- A native callback is never a managed delegate that native code keeps: Gir.Core does not keep such delegates alive. Use an `[UnmanagedCallersOnly]` function pointer.
- S/MIME certificates live in the per-user .NET `X509Store`s `wino-mail-personal` and `wino-mail-recipient` (owner decision). Their private keys are protected by file permissions only; the Linux guide says so, and nothing here may claim keyring protection.
- Never fix a bug in shared code from this project; record a handoff in the [port plan](../../docs/linux-port-plan.md#open-handoffs-to-shared-code).
- No `MsalCacheHelper`: it binds libsecret through variadic functions. The Outlook cache goes through `ISecureItemStore`.
- Create `GtkDispatcher` on the GTK main thread. Work from that thread runs inline; other threads queue on the main context.
- When shared code gains a contract a head must supply, register it in `LinuxPlatformRegistration.cs` in the same change.

## Check your change

Build through the head (`dotnet build src/Wino.Mail.Linux/Wino.Mail.Linux.csproj`), in Debug and in Release. Then start the app in a session with an unlocked keyring and confirm the changed service in the running app; the Linux guide has the commands. To check the keyring item itself:

```sh
secret-tool search application app.winomail.Mail
```

It prints the item's attributes and its secret, so do not paste the output anywhere.
