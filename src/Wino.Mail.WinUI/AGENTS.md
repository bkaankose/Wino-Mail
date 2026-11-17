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
- `src/Wino.Mail.WinUI/Services/DialogService.cs` (with `DialogServiceBase`) is the only code that creates or presents dialogs. It returns the result through an async method. Pages, controls, and other services call `IMailDialogService`, `IDialogServiceBase`, or the Windows-only `IWinoDialogService`; they never call `ShowAsync` or set `XamlRoot`. Code in `Wino.Services`, `Wino.Core` and `Wino.Authentication` cannot use a dialog contract at all; `WINOUI001` fails the build.
- WinUI allows one open `ContentDialog` per `XamlRoot`. Queued dialogs go through `HandleDialogPresentationAsync`. Progress dialogs that their owner closes go through `StartSessionDialog`.

### Native AOT

- Every value that reaches `ItemsSource`, `MenuItemsSource`, `FooterMenuItemsSource`, `TabItemsSource` or `CollectionViewSource.Source` has a concrete static type: an array, `List<T>`, `ObservableCollection<T>` or a `[GeneratedWinRTExposedType]` subclass. An interface type there throws in Release. `WINOAOT001` fails the build; fix the type.
- Release treats trim, AOT, single-file and CsWinRT warnings as errors. Fix the cause. The only allowed suppressions are the allowlist in `build/Wino.Windows.targets`; add nothing to `NoWarn`.
- Fix CsWinRT1034 and CsWinRT1035 with `[WinRT.DynamicWindowsRuntimeCast(typeof(T))]`.
- A runtime `{Binding}` needs every CLR object in its path audited and marked for CsWinRT. Use compiled `x:Bind` and the audit is not needed.

## How a page consumes a ViewModel

A page derives from `BasePage<TViewModel>` ([BasePage.cs](BasePage.cs)) through a small abstract class in `Views/Abstract`, because XAML cannot name a generic base:

```csharp
// Views/Abstract/AccountUnreadBadgePageAbstract.cs
public abstract class AccountUnreadBadgePageAbstract : SettingsPageBase<AccountUnreadBadgePageViewModel> { }
```

```xml
<abstract:AccountUnreadBadgePageAbstract x:Class="Wino.Views.Settings.AccountUnreadBadgePage" ...>
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
- Put a new operating-system service that needs no XAML type and no window in `Wino.Platform.Windows`, behind a contract in `Wino.Core.Domain`. Services that need XAML stay in `Services/`.
- Register services in [CoreUWPContainerSetup.cs](CoreUWPContainerSetup.cs) or `ConfigureServices` in [App.xaml.cs](App.xaml.cs). A class that implements several interfaces is registered once and each interface forwards to it, so there is one instance.
- Wire `Loaded`, `Unloaded` and input events in XAML. Keep visual composition out of `.xaml.cs`.
- Use `XamlHelpers` functions where a converter would go, `WinoFontIcon` for every icon, and `{ThemeResource}` for brushes.
- Format changed XAML before building: `pwsh -File scripts/maintenance/windows/format-xaml.ps1 -Changed`, and run it with `-Check` before handoff.

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
