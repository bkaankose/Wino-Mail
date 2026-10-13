# A: shared ViewModel registration and NavigationCoordinator

Status: T1 and T2 are done. Nothing is committed. Manual Mac runtime checks are still pending.

## Changed files

T1 (shared ViewModel registration). The orchestrator drafted this; I reviewed and fixed it.
- `src/Wino.Mail.ViewModels/WinoViewModelRegistration.cs` (new): `RegisterWinoViewModels()`. Fix: added the missing `using System;`, which `Lazy<>` needs.
- `src/Wino.Mail.ViewModels/Wino.Mail.ViewModels.csproj`: adds `Microsoft.Extensions.DependencyInjection.Abstractions`, whose version is already pinned in `Directory.Packages.props`.
- `src/Wino.Mail.WinUI/App.xaml.cs` and `CoreUWPContainerSetup.cs`: `RegisterViewModels` now calls `RegisterWinoViewModels()` plus the Windows-only `MigrationPageViewModel`, and `RegisterCoreViewModels` is gone. Fix: removed a UTF-8 BOM that the draft had added to both files.
- `src/Wino.Mail.MacOS/Infrastructure/MacViewModelRegistration*.cs`: the shared ViewModel lines are removed. Fixes: tidied stray blank lines and comments in the Calendar, Mail and Settings partials. The Activation, Notifications, Settings.WinoAccount and Tasks partials show as modified in `git status` but have no content diff.
- Audit: I compared every ViewModel registered at HEAD in WinUI `App.xaml.cs`, `CoreUWPContainerSetup.RegisterCoreViewModels` and all Mac partials against the shared list. All of them are present with the WinUI lifetimes, so none are missing. Mac lifetimes already matched.

T2 (NavigationCoordinator)
- `src/Wino.Mail.ViewModels/Navigation/INavigationHost.cs` (new): the `NavigationHostKind` enum (`Window`, `ShellContent`, `RenderingFrame`, `Settings`) and the `INavigationHost` presentation contract.
- `src/Wino.Mail.ViewModels/Navigation/NavigationCoordinator.cs` (new): `sealed partial`. It has to be `partial` because CsWinRT1028 fails the Release gate otherwise.
- `src/Wino.Mail.MacOS/Infrastructure/AppKitNavigationService.cs`: now an adapter. It implements `INavigationHost` explicitly and forwards `INavigationService` to the coordinator.
- `tests/Wino.Mail.ViewModels.Tests/NavigationCoordinatorTests.cs` (new): 13 tests that use a fake host.

## Coordinator public surface

`NavigationCoordinator(INavigationHost host, IServiceProvider services, IDispatcher dispatcher, IMessenger? messenger = null)` implements `INavigationService` and `IDisposable`, and receives five messages: `BreadcrumbNavigationRequested`, `BackBreadcrumNavigationRequested`, `AccountCreatedMessage`, `WelcomeImportCompletedMessage` and `AccountRemovedMessage`.

Members beyond `INavigationService`:
- `NavigateAsync`, `ShowShellAsync`, `EnsureShellAsync(mode, parameter)`, `IsModeAvailable`, `StopAsync`
- `OnSecondaryWindowClosedByUser()`
- `CanQuit`, `IsShellReady`, `CurrentPage`, `CurrentParameter`, `PendingNavigation`
- `ChangeApplicationMode(mode, ShellModeActivationContext?)`

`IServiceProvider` resolves `IWinoLogger`, `IAccountService`, `IApplicationRuntime` and `WelcomeWizardContext` lazily, as before.

`INavigationHost` is the part a head implements:
- **Routes and state:** `TryGetRoute`, `GetPageType`, and the state flags (`IsShellShown`, `HasMainContent`, `MainContentHasPendingWork`, `ShellContentViewModel`, `IsSecondaryWindowShown`, `IsSettingsAvailable`).
- **Presentation:** `ShowInRenderingFrameAsync`, `SetShellContentAsync`, `ClearShellContentAsync`, `ReplaceMainContentAsync(…, Action committed)`, `ShowInSecondaryWindowAsync(…, Action committed)`, `CloseSecondaryWindowAsync(closedByUser)`, `PresentShellAsync(Action committed) → Func<Task>` (the shell's first activation, which runs outside the gate), `BringShellToFrontAsync`, `ActivateShellModeAsync`, `DismissShellSurfacesAsync`, `ReleaseMainContentAsync`.
- **Settings:** `ShowSettingsAsync`, `TrySettingsGoBack`, `CloseSettingsAsync`, `StopSettingsAsync`.

The `committed` callbacks keep the old commit point: route state and history change once the native page is hosted, before it is activated.

## What moved and what stayed

- **Moved to ViewModels:**
  - the main and secondary history stacks, current route, `NavigationResult` and the gate
  - `IConfirmBackNavigation` and `IBackNavigationAware` handling
  - the five message recipients
  - account created → close the secondary surface, show the shell, then `SynchronizeCreatedAccountAsync`
  - last account removed → reset the wizard and return to Welcome
  - `IsModeAvailable`, `CanQuit`, the "setup owns the main window" guard in `EnsureShellAsync`, and the stopping and fire-and-forget logic
- **Stayed in the Mac head:**
  - creating, hosting, activating, releasing and disposing controllers
  - the shell, shell content and rendering-host references
  - the secondary `WelcomeWindow` and its WillClose observer (it now calls `OnSecondaryWindowClosedByUser`)
  - `BringToFront`, `CloseAuxiliaryWindows`, `ISettingsWindowPresenter`, `MacPageRegistry` and `MacPageHost`
- `MacPageHost` is mapped to `NavigationHostKind` inside the adapter, so the registry partials are unchanged. The public surface the Mac views use is unchanged.
- The WinUI `NavigationService` is unchanged. It could adopt the coordinator later for the account-removed and account-created reactions and back confirmation (today `App.HandleAccountRemovedAsync` and `App.Receive(AccountCreatedMessage)`), but its Frame-based history would need a host that tolerates Frame-owned back stacks.

## Behaviour notes

- Exceptions logged from fire-and-forget navigation now carry the operation name `NavigationCoordinator` instead of `AppKitNavigationService`.
- I kept the existing quirk where `ClearShellContentAsync` does not reset the cached shell-content controller. That controller is what back confirmation consults until the next shell navigation.
- The coordinator's own new code uses no null-forgiving operators. The adapter's `null!` placeholders were replaced, and `ChangeApplicationMode(mode)` no longer passes `null!`.

## Verification

All logs are in `artifacts/ios-platform/`.

| Command | Result |
| --- | --- |
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64` | 0 errors, 0 warnings (`A-winui-debug.log`) |
| `dotnet build … -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` | 0 errors, 0 warnings (`A-winui-release.log`). The first run failed with CsWinRT1028; marking the coordinator `partial` fixed it. |
| `dotnet test tests/Wino.Mail.ViewModels.Tests -c Debug -p:Platform=x64` (Windows) | 170 passed, 0 failed (`A-tests-windows.log`) |
| Mac `dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 … -p:CodesignKey=- -p:CodesignProvision=` in `~/Desktop/wt-A` | 0 errors, 48 warnings (`A-mac-debug-arm64.log`). None are new: the only warning in a changed file is CA1422 on the unchanged `BringToFront` (`ActivateIgnoringOtherApps`). |
| Mac `dotnet test tests/Wino.Mail.ViewModels.Tests -c Debug` | 170 passed, 0 failed (`A-tests-mac.log`) |

The baseline was 144 passed and 11 failed `WinoIntelligenceHeaderPresenterTests`. Now: 157 existing tests plus 13 new ones pass, and all 13 `WinoIntelligenceHeaderPresenterTests` pass on both hosts. I did not investigate why the 11 baseline failures no longer reproduce.

## Mirror and Mac build gotchas (for the other agents)

- Exclude `./.claude` from the tar mirror. It contains the other agents' worktrees (gigabytes). `src/Wino.Mail.WinUI` still mirrors around 3 GB of untracked output, so the mirror is slow (over 10 minutes).
- The Windows checkout stores the xcframework `Versions/Current` symlinks as text files. The Mac build then fails with MT7091 "not a valid framework" for Sparkle and WinoStoreKit2. Workaround: copy `src/Wino.Core.MacOS.Bindings/{Sparkle,StoreKit2}/Native/*.xcframework` from the Mac checkout `~/Desktop/Wino-Mail` (read-only use; identical to HEAD), then delete `obj` in `Wino.Mail.MacOS` and `Wino.Core.MacOS.Bindings` before building.

## Pending and risks

- **Manual Mac runtime smoke (pending):**
  - onboarding → shell
  - Settings › Add account in a secondary window: back, close by the user, and the account created there
  - removing the last account returns to Welcome and closes the auxiliary windows
  - back confirmation on the contact and event editors
  - Dock, URL and notification activation through `EnsureShellAsync`
  - quit being blocked during account setup
- The macOS x64 build was not run.
- **For other agents:** there are no new DI registrations. Agents that touch `MacActivationCoordinator` or `MacNotificationResponseHandler` keep using `AppKitNavigationService` with the same members. A future UIKit head constructs `new NavigationCoordinator(this, services, dispatcher)` and implements `INavigationHost`.
