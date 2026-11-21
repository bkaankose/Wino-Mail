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

A view is a plain class that builds its widgets in the constructor and exposes `Root`. There is no XML, no Blueprint and no GObject subclass except `ManagedItem`, the list item type.

- A routed view derives from `WinoView<TViewModel>` (`Infrastructure/WinoView.cs`) and is created with `PageScopes.Create<T>(services)`: the view and its transient ViewModel live in a DI scope of their own, which `ReleaseAsync` disposes. Views are not registered in the container; a view needs a public constructor whose parameters are container services, the ViewModel first. The view binds through `Bind` (a `GtkBinder` from `Wino.Presentation.Linux`) and `Bindings`, subscribes to messages with `Subscribe`, and overrides `InitializeAsync` and `DeactivateAsync`. `MailListView` is the example.
- `GtkNavigationService` is the host of the shared `NavigationCoordinator`. Routes are in `Infrastructure/LinuxPageRegistry.<Area>.cs` (`Register<TView>(page, host)`), head services in `Infrastructure/LinuxViewModelRegistration.<Area>.cs`; a new page edits neither `LinuxComposition.cs` nor `GtkNavigationService.cs`. A page without a route is refused. The reading pane (`ReadingPaneHost`) belongs to whoever calls `AttachRenderingHost`, today the shell; it keeps a page only when the view implements `IRenavigableView` (the reader) or is the idle view, and releases any other page when the next one shows.
- `ShellLayout` (`Views/Shell`) owns the panes and their widths; `ShellMetrics` holds every size. The sidebar docks from 860 px and is an overlay below; the list and the reader stack when the content area is narrower than the list and reader minimums together. The sidebar width and the window placement are kept by `WindowStateStore`, the list width by `IStatePersistanceService.MailListPaneLength`.
- A content page shown beside the reading pane implements `IUsesReadingPane`; any other page gets the whole content area and may split it with `ListDetailSplit`. Pages in the content area have no header bar with window buttons: the shell's one header spans the content area.
- The shell's one header is `ShellHeaderBar` (`Views/Shell`). The content view may implement `IShellSearchTarget` (`Infrastructure/ShellContracts.cs`) to receive the header search entry. Header buttons for Daily briefing, What's New and Wino Account appear when their presenter is registered. Application actions are in `Infrastructure/AppActions.cs`; add an accelerator there, not in a view.
- The sidebar is `ShellSidebarView`, a `Gtk.ListView` over `ShellMenuRowPresentation.Flatten`/`Describe` drawn by `WinoShellRow`. A row is invoked by a selection change (pointer, accessibility clients) or by Enter; the arrow keys only move the focus, Left and Right collapse and expand. Other packages extend it through `CellSetup`/`CellTeardown`, `RowsChanged`, `AddRowProvider` and partial files of the class.
- Sidebar menus are data (`ShellSidebarContextMenus.Build`); other areas add entries with `AddExtension`, once at startup. `ShellSidebarMenuPresenter` shows them through `ContextMenu`; a menu action runs after its popover closed.
- The mail list sends `ActiveMailFolderChangedEvent` after `OnNavigatedTo` and awaits `WaitForCurrentFolderInitializationAsync`. Without it the shell never shows its menu.
- Rows come from shared presentation code: `ShellMenuRowPresentation` for the sidebar, `MailListProjection`, `MailRowPresentation` and `MailListSelectionSnapshotBuilder` for the list. A cell only renders.
- New lists use `ObservableListModel<T>`, `WinoCellFactory` and `ListSections` from `Wino.Presentation.Linux.Lists`; the mail list still uses its `Gtk.StringList` of row keys until ML-02. A page disposes its factories, sections and model when it is released (`Bindings.Own`).

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

- Every Wino concept glyph is a `WinoIcon` or `WinoIcons.Label`/`Button` from `Wino.Presentation.Linux`. Adwaita icon names only for GNOME chrome (back, close, menu, window controls, expanders) and for widgets that take an icon name only.
- Touch widgets on the GTK main thread only. ViewModel notifications can arrive on other threads; go through `IDispatcher` or `GLib.Functions.IdleAdd`.
- Connect GTK signals only through `GtkSignals` (`Connect`, `OnClicked`, `OnNotify`, ...) or `GtkBinder`, and own the returned subscription in `Bindings`. Gir.Core 0.8.1 keeps a handler in the object's closure table after `-=`, so a raw `+=` whose handler captures the view keeps it alive with its widgets; the helpers reach the handler through a box that disposing empties. `MailListView` is the example. Raw `+=` is fine only for handlers that live as long as the app.
- Initialize a Gir.Core module before its first use when nothing else does: `WebKit.Module.Initialize()` runs in the reader session. Without it the native library is not found.
- Take user-visible text from `Translator` when the string exists. The proof of concept has a few English status texts of its own (`LinuxApplication`, `GtkApplicationLifecycleHost`); move them to `resources.json` before the head is released.
- Register a new head service in its area's `Infrastructure/LinuxViewModelRegistration.<Area>.cs` hook, never in [LinuxComposition.cs](LinuxComposition.cs); platform services go to `Wino.Platform.Linux`.
- A dialog without a screen stays in `GtkDialogService.Dismissed.cs`. Its package moves it into its own `GtkDialogService.<Group>.cs` and builds it on `AlertAsync(AlertRequest)` (`Views/Dialogs/AlertPresenter`), `FormDialog` or `FilePickers`, presented through `PresentAsync(owner => ..., dismissed)` so it runs on the main thread and hangs from the active window. Files and folders are picked only through `FilePickers`.
- Configurable shortcuts are routed by `ShortcutRouter` (one capture-phase key controller per window, owned for the main window by `ShortcutHost`). A page handles them in its ViewModel's `KeyboardShortcutHook`; views add no key handler for a configurable action. Fixed keys are application actions with accelerators. A secondary window creates its own router with `IsPopOut`.
- A window besides the main window is a `SecondaryWindow` (`Views/SecondaryWindow.cs`) registered with `WindowManager` under a key (`mail:<id>`, `composer:<draftId>`, `event:<id>`, `whatsnew`, `onboarding`); its view is released in `Closing`, and stop closes every window first. The Settings window keeps its own presenter.
- Window-level messages go through `IMailDialogService.InfoBarMessage` (or the `InfoBarMessageRequested` message) to the window's `InfoBarHost`; a view never creates a host.

## Check your change

- **On a Linux host:** the Debug build, the Release build (the gate), then manual checks in a GNOME Wayland session with a leased lab account. Commands, the keyring a development session needs, and the headless session for screenshots are in the Linux guide.
- **On a Windows or Mac machine:** build the shared projects you touched and report "Linux Debug and Release builds and manual checks pending" with the list of changed files.

A build does not prove UI behavior.

## Performance

- Coalesce rebuilds: the list rebuilds once per main-loop turn (a `MainLoop.Coalescer` or `QueueRebuild`), not once per notification. In the sidebar a menu item's property change repaints one row; only a structure change re-flattens, once per main-loop turn.
- Appending a loaded page must stay an append to the list model, so the scroll position is kept.
- The reader reuses one web view and one ViewModel for every message.
- Sender pictures come from `LinuxSenderPictureProvider`; never decode an image in a cell bind.
