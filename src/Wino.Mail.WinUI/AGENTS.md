# Wino.Mail.WinUI

The Windows app: WinUI 3 pages in XAML over the shared services and ViewModels, shipped as an MSIX package compiled with Native AOT and full trimming. This file is binding for `src/Wino.Mail.WinUI` and `src/Wino.Platform.Windows`.

## Read first

| When you | Read |
| --- | --- |
| Change any C# or XAML here | "Native AOT and build safety" in [Windows development](../../docs/harness/development-windows.md). It is release-blocking. |
| Write XAML, a control or an icon | "WinUI and XAML rules" and "Icons" in the [implementation rules](../../docs/harness/implementation-rules.md) |
| Add a screen or a visual pattern | [Wino design guideline](../../docs/wino-design-guideline.md) |
| Change a reusable control | [controls/AGENTS.md](../../controls/AGENTS.md) |
| Touch sign-in or an OAuth client | "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md) |

## Hard stops

### Dialogs

- Every `ContentDialog` in the WinUI head is a dedicated `ContentDialog` file in `src/Wino.Mail.WinUI/Dialogs`, with its text from `Translator`. Never write `new ContentDialog { ... }` or declare a `<ContentDialog>` inside a page or control.
- `src/Wino.Mail.WinUI/Services/DialogService.cs` (with `DialogServiceBase`) is the only code that creates or presents dialogs. It returns the result through an async method. Pages, controls, and other services call `IMailDialogService`, `IDialogServiceBase`, or the Windows-only `IWinoDialogService`; they never call `ShowAsync` or set `XamlRoot`. Code in `Wino.Services`, `Wino.Core`, `Wino.Authentication`, the provider projects, `Wino.Agent.Runtime` and `Wino.Core.ML` cannot use a dialog contract at all, not even through `GetService<T>()`; `WINOUI001` fails the build.
- WinUI allows one open `ContentDialog` per `XamlRoot`. Queued dialogs go through `HandleDialogPresentationAsync`. Progress dialogs that their owner closes go through `StartSessionDialog`.

### Native AOT

- Every value that reaches `ItemsSource`, `MenuItemsSource`, `FooterMenuItemsSource`, `TabItemsSource` or `CollectionViewSource.Source` has a concrete static type: an array, `List<T>`, `ObservableCollection<T>` or a concrete collection class. An interface type there throws in Release. `WINOAOT001` fails the build; fix the type.
- Release treats trim, AOT, single-file and CsWinRT warnings as errors. Fix the cause. The only allowed suppressions are the allowlist in `build/Wino.Windows.targets`; add nothing to `NoWarn`.
- Fix CsWinRT1034 and CsWinRT1035 with `[WinRT.DynamicWindowsRuntimeCast(typeof(T))]`.
- A shared type that WinUI receives must be listed with `[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(T))]`, or lists come up empty in Release. Types from `Wino.Mail.ViewModels` go in [WinRTExposedTypes.cs](WinRTExposedTypes.cs); types from `Wino.Mail.Controls.Core` go in `controls/Wino.Mail.Controls.WinUI/WinRTExposedTypes.cs`; a closed generic goes in the app that names its type argument. The portable projects carry no CsWinRT attribute, and they no longer generate anything themselves in Release. So list every shared type a view can receive: a list item that raises change notifications, a group header, and the concrete collection a bound property really holds (`List<T>`, `T[]`, `ReadOnlyCollection<T>`), each closed generic separately.
- A runtime `{Binding}` must never pass through a shared type: it fails silently in Release. Use compiled `x:Bind`, or mirror the value onto a dependency property of the view, as `ApplicationThemeEditorPage` does.
- A class in `Wino.Platform.Windows` that implements a WinRT interface or is handed to WinRT must be `partial`; CsWinRT reports `CsWinRT1028` in Release otherwise.

## Mail renderer

Mail is read, printed and composed with the native mail renderer (`Wino.Renderer`), not with WebView2. The engine is a separate repository, referenced as the `WinoMail.Renderer.Windows` package (which brings `WinoMail.Renderer`) by `build/Wino.Renderer.props`; `WinoRendererRoot` or `WINO_RENDERER_ROOT` builds against a renderer checkout instead.

- The glue every head on the renderer shares is in [src/Wino.Mail.Rendering](../Wino.Mail.Rendering/AGENTS.md): `MailEngineHost`, the reader and editor sessions, the reader presentation, the unsubscribe reader and the spell-check languages. It talks to the engine's `IMailView`, which the WinUI `MailView` implements. Change it there, not in this head.
- `MailEngineHost` owns the one platform backend and the one `MailEngine` of the process. [CoreUWPContainerSetup.cs](CoreUWPContainerSetup.cs) registers it through `AddMailRendering()` with this head's `Rendering/WindowsMailEngineBackendFactory`. Never create a second engine.
- The shared classes implement the seams, so ViewModels never see an engine type:

  | Class | Seam |
  | --- | --- |
  | `EngineMailMessageReader` | `IMailMessageReader` (reading pane, from the MIME) |
  | `EngineHtmlReaderSession` | `IHtmlMailReaderSession` (HTML-only surfaces: the event description) |
  | `EngineMailEditorSession`, hosted by this head's `Rendering/EngineMailEditorView` element | `IHtmlMailEditorSession`, `IComposeBodyWriter` (composer, signature dialog, template page, event notes) |
  | `Rendering/EngineMailPrintPresenter` (this head) | `IMailPrintPresenter` (print and Save as PDF); the page options come from the shared `EnginePrintDocuments` |
  | `EngineReaderPresentation` | Theme, typography and high contrast of a reader view; `Rendering/ReaderPresentationWatching` feeds it the window's high contrast state |

- What stays here is WinUI only: the `EngineMailEditorView` element and its context menu, the print dialog presenter, the window high contrast watcher and the backend factory.
- The engine's `MailView` is created in code and placed into a host element: the reader pages create it themselves, the editor through `EngineMailEditorView`. It is not declared in XAML.
- Links, remote content and tracking-parameter stripping on Windows are the engine's. `LinkSanitizer` and the HTML string pipeline (`GetMailRenderModel`) serve the other heads.
- Never bring WebView2 back for reading or composing, and add no fallback reader. WebView2 remains only in `EmbeddedBrowserSignInDialog` and the two policy dialogs, which load web pages.
- The Windows app must not reference `Wino.Editor.Core`; it references `controls/Wino.Editor.Abstractions` only.
- An engine fault is fixed in the renderer repository (zero warnings there), not hidden in this head.

## How a page consumes a ViewModel

A page derives from `BasePage<TViewModel>` ([BasePage.cs](BasePage.cs)) through a small abstract class in `Views/Abstract`, because XAML cannot name a generic base:

```csharp
// Views/Abstract/AccountUnreadBadgePageAbstract.cs
public abstract class AccountUnreadBadgePageAbstract : SettingsPageBase<AccountUnreadBadgePageViewModel> { }
```

```xml
<abstract:AccountUnreadBadgePageAbstract x:Class="Wino.Mail.WinUI.Views.Settings.AccountUnreadBadgePage" ...>
    <ToggleSwitch IsOn="{x:Bind ViewModel.IsAccountBadgeEnabled, Mode=TwoWay}" />
    <ItemsRepeater ItemsSource="{x:Bind ViewModel.Folders, Mode=OneWay}" />
    <Button Command="{x:Bind ViewModel.RestoreDefaultsCommand}" />
</abstract:AccountUnreadBadgePageAbstract>
```

- `BasePage<T>` resolves `ViewModel` from the container, assigns `ViewModel.Dispatcher`, and calls `OnNavigatedTo`, `OnPageLoaded` and `OnNavigatedFrom`. The code-behind only calls `InitializeComponent()`.
- Bind with `x:Bind`. It is used 6,300 times here against 207 `{Binding}`.
- A page reads state and invokes commands on its ViewModel. When a page needs data the ViewModel does not expose, add it to the shared ViewModel; 31 code-behind files still resolve a service through `WinoApplication.Current.Services`, and new code does not add to them.

## Always

- Put a business rule in shared services or ViewModels. This head adapts them.
- Start and stop the backend only through `ApplicationLifecycleCoordinator`; the Windows side of that sequence is in [App.Lifecycle.cs](App.Lifecycle.cs). Opening or closing a window, and close-to-tray, never start or stop it.
- Parse a toast activation here, then hand the action to the shared `NotificationResponseService`. Build no request and no draft in this head.
- Anything that puts shell content on screen goes through `IWinoShellWindow.RunWhenUnlockedAsync`, so a locked window shows the lock screen first. The app lock configuration lives only in `ISecureItemStore` under the scope `app-lock`; it never enters preferences, backups or Wino Account sync.
- Put a new operating-system service that needs no XAML type and no window in `Wino.Platform.Windows`, behind a contract in `Wino.Core.Domain`. Register it in `WindowsPlatformRegistration.AddWindowsPlatformServices`. When it needs the main window handle or the UI thread, it takes `IWindowsMainWindowHost` or `IWindowsThreadQueue`, which are declared in `Wino.Platform.Windows` and implemented here; a contract in `Wino.Core.Domain` never carries a window handle. Services that need XAML, a window, the controls or a ViewModel type stay in `Services/`.
- Register services in [CoreUWPContainerSetup.cs](CoreUWPContainerSetup.cs) or `ConfigureServices` in [App.xaml.cs](App.xaml.cs). A class that implements several interfaces is registered once and each interface forwards to it, so there is one instance.
- Wire `Loaded`, `Unloaded` and input events in XAML. Keep visual composition out of `.xaml.cs`.
- Use `XamlHelpers` functions where a converter would go, `WinoFontIcon` for every icon, and `{ThemeResource}` for brushes.
- Format changed XAML before building. In Claude Code the project hook in `.claude/settings.json` formats every `.xaml` file right after it is written (not `.xaml.cs`); other editors run `pwsh -File scripts/maintenance/windows/format-xaml.ps1 -Changed`. Run it with `-Check` before handoff.

## Check your change

- **On a Windows machine:**
  1. Debug build, x64.
  2. Behavior in the running Debug app with `winapp ui` (status, search, invoke, wait-for, screenshot, record) against leased lab accounts. The commands are under "Agent UI checks" in the Windows guide.
  3. The Release gate, without launching or deploying:

     ```powershell
     dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
     ```
- **On any other machine:** this project does not build there, and XAML is not compiled. Build the shared projects you touched, follow the pattern above for the page, and report "Windows Debug build, Release gate and UI check pending" with the list of changed files.

VS Code F5 uses the approved `scripts/development/windows/start-wino.ps1` helper to reuse unchanged Debug x64 output. It is the only development wrapper and input-hash cache; keep custom debug build targets out of the project.

A build does not prove UI behavior. Report a UI check as done only when it was performed, and say whether an agent did it through `winapp ui` or a person did.
