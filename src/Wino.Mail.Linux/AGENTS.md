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

- A routed view derives from `WinoView<TViewModel>` (`Infrastructure/WinoView.cs`) and is created with `PageScopes.Create<T>(services)`: the view and its transient ViewModel live in a DI scope of their own, which `ReleaseAsync` disposes. Views are not registered in the container; a view needs a public constructor whose parameters are container services, the ViewModel first. The view binds through `Bind` (a `GtkBinder` from `Wino.Presentation.Linux`) and `Bindings`, subscribes to messages with `Subscribe`, and overrides `InitializeAsync` and `DeactivateAsync`. `MailListView` is the example.
- `GtkNavigationService` is the host of the shared `NavigationCoordinator`. Routes are in `Infrastructure/LinuxPageRegistry.<Area>.cs` (`Register<TView>(page, host)`), head services in `Infrastructure/LinuxViewModelRegistration.<Area>.cs`; a new page edits neither `LinuxComposition.cs` nor `GtkNavigationService.cs`. A page without a route is refused. The reading pane (`ReadingPaneHost`) belongs to whoever calls `AttachRenderingHost`, today the shell; it keeps a page only when the view implements `IRenavigableView` (the reader) or is the idle view, and releases any other page when the next one shows.
- The mail list sends `ActiveMailFolderChangedEvent` after `OnNavigatedTo` and awaits `WaitForCurrentFolderInitializationAsync`. Without it the shell never shows its menu.
- Rows come from shared presentation code: `ShellMenuRowPresentation` for the sidebar, `MailListProjection`, `MailRowPresentation` and `MailListSelectionSnapshotBuilder` for the list. A cell only renders.
- A `Gtk.ListView` model holds row keys in a `Gtk.StringList`; a cell finds its row by position.

```csharp
public MailListView(MailListPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
{
    ViewModel.MailCollection.CoreDispatcher = Dispatcher;
    _projection = new MailListProjection(ViewModel.MailCollection.Items, ViewModel.MailListOptions ?? new MailListProjectionOptions());
    _projection.ProjectionChanged += (_, _) => QueueRebuild();
    Bind.Bind(nameof(MailListPageViewModel.MailListOptions), static vm => vm.MailListOptions, options => _projection.SetOptions(options ?? new MailListProjectionOptions()));
}
```

## Always

- Touch widgets on the GTK main thread only. ViewModel notifications can arrive on other threads; go through `IDispatcher` or `GLib.Functions.IdleAdd`.
- A GTK signal handler of a routed view must not capture the view. Gir.Core 0.8.1 keeps a handler in the object's closure table after `-=`, so a captured view stays alive with its widgets. Reach the view through a box that `DeactivateAsync` or `Bindings` empties, as `MailListView` does, and still disconnect with `-=`.
- Initialize a Gir.Core module before its first use when nothing else does: `WebKit.Module.Initialize()` runs in the reader session. Without it the native library is not found.
- Take user-visible text from `Translator` when the string exists. The proof of concept has a few English status texts of its own (`LinuxApplication`, `GtkApplicationLifecycleHost`); move them to `resources.json` before the head is released.
- Register a new head service in its area's `Infrastructure/LinuxViewModelRegistration.<Area>.cs` hook, never in [LinuxComposition.cs](LinuxComposition.cs); platform services go to `Wino.Platform.Linux`.
- A dialog without a screen answers as dismissed in `GtkDialogService.Dismissed.cs`. Give it a real implementation in `GtkDialogService.cs` when you add its screen.

## Check your change

- **On a Linux host:** the Debug build, the Release build (the gate), then manual checks in a GNOME Wayland session with a leased lab account. Commands, the keyring a development session needs, and the headless session for screenshots are in the Linux guide.
- **On a Windows or Mac machine:** build the shared projects you touched and report "Linux Debug and Release builds and manual checks pending" with the list of changed files.

A build does not prove UI behavior.

## Performance

- Coalesce rebuilds: the sidebar and the list rebuild once per main-loop turn (`QueueRebuild`), not once per notification.
- Appending a loaded page must stay an append to the list model, so the scroll position is kept.
- The reader reuses one web view and one ViewModel for every message.
