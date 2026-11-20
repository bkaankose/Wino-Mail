# Wino.Mail.Linux

The Linux app, as a proof of concept for issue #1216: GTK 4 and libadwaita through Gir.Core, with WebKitGTK 6.0 for the reader, written in C# over the shared services and ViewModels. It runs on CoreCLR. It targets GNOME on Wayland (Fedora and Ubuntu); tiling compositors are out of scope. It shows the shell, the folder list, the mail list and the reader, and nothing else. It is not released. This file is binding for `src/Wino.Mail.Linux`; platform services are in [Wino.Platform.Linux](../Wino.Platform.Linux/AGENTS.md).

## Read first

| When you | Read |
| --- | --- |
| Build, run, use a headless session or check the app | [Linux development](../../docs/harness/development-linux.md) |
| Touch sign-in or an OAuth client | "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md) |
| Change the reader session | [controls/AGENTS.md](../../controls/AGENTS.md) |

## Hard stops

- No Uno, Avalonia or MAUI. The UI is GTK widgets built in code.
- No test projects, and no reference to `Wino.Hosting`: no app may reference it.
- Shared projects get no Linux-only change. They must still build for Windows.
- Gmail and Outlook sign-in do not exist here. Never use the Google UWP OAuth client for it; the rule is in the root guidance.
- The application id `app.winomail.Mail` (`Program.ApplicationId`) is the GApplication id, the name of the `.desktop` file and the application attribute of the keyring item. Never change it.
- `--lab-lease` works in Debug builds only (`LabAccountBootstrap`). Never log the lab password.
- This project is plain `net10.0`, so the shared Release gate applies: Native AOT, trimming and single-file analyzers with warnings as errors. Keep it clean; do not exempt the project.

## How a view consumes a ViewModel

A view is a plain class that builds its widgets in the constructor and exposes `Root`. There is no XML, no Blueprint and no GObject subclass.

- The view resolves its ViewModel from the container and assigns `Dispatcher`, then calls `OnNavigatedTo`, `OnPageLoaded` and, when released, `OnNavigatedFrom`.
- `GtkNavigationService` is the host of the shared `NavigationCoordinator`. It routes `MailListPage` to the shell's content and `MailRenderingPage` and `IdlePage` to the reading pane. A page without a route is refused; add the route in `TryGetRoute` together with its view.
- The mail list sends `ActiveMailFolderChangedEvent` after `OnNavigatedTo` and awaits `WaitForCurrentFolderInitializationAsync`. Without it the shell never shows its menu.
- Rows come from shared presentation code: `ShellMenuRowPresentation` for the sidebar, `MailListProjection`, `MailRowPresentation` and `MailListSelectionSnapshotBuilder` for the list. A cell only renders.
- A `Gtk.ListView` model holds row keys in a `Gtk.StringList`; a cell finds its row by position.

```csharp
ViewModel = services.GetRequiredService<MailListPageViewModel>();
ViewModel.Dispatcher = dispatcher;
ViewModel.MailCollection.CoreDispatcher = dispatcher;
_projection = new MailListProjection(ViewModel.MailCollection.Items, ViewModel.MailListOptions ?? new MailListProjectionOptions());
_projection.ProjectionChanged += (_, _) => QueueRebuild();
```

## Always

- Touch widgets on the GTK main thread only. ViewModel notifications can arrive on other threads; go through `IDispatcher` or `GLib.Functions.IdleAdd`.
- Initialize a Gir.Core module before its first use when nothing else does: `WebKit.Module.Initialize()` runs in the reader session. Without it the native library is not found.
- Take user-visible text from `Translator` when the string exists. The proof of concept has a few English status texts of its own (`LinuxApplication`, `GtkApplicationLifecycleHost`); move them to `resources.json` before the head is released.
- Register every new service in [LinuxComposition.cs](LinuxComposition.cs); platform services go to `Wino.Platform.Linux`.
- A dialog without a screen answers as dismissed in `GtkDialogService.Dismissed.cs`. Give it a real implementation in `GtkDialogService.cs` when you add its screen.

## Check your change

- **On a Linux host:** the Debug build, the Release build (the gate), then manual checks in a GNOME Wayland session with a leased lab account. Commands, the keyring a development session needs, and the headless session for screenshots are in the Linux guide.
- **On a Windows or Mac machine:** build the shared projects you touched and report "Linux Debug and Release builds and manual checks pending" with the list of changed files.

A build does not prove UI behavior.

## Performance

- Coalesce rebuilds: the sidebar and the list rebuild once per main-loop turn (`QueueRebuild`), not once per notification.
- Appending a loaded page must stay an append to the list model, so the scroll position is kept.
- The reader reuses one web view and one ViewModel for every message.
