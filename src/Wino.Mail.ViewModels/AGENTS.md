# Wino.Mail.ViewModels

The one ViewModel library for all four apps. A ViewModel holds the state a screen shows and the commands it invokes. The WinUI, AppKit, UIKit and Android views consume the same classes, so a change here changes four apps.

## Hard stops

- **No UI framework type.** Nothing from WinUI, AppKit, UIKit or Android, and no hook for one platform. There is no platform copy of a production ViewModel.
- **A ViewModel holds UI state only.** Business rules and data access go in a service. An action that changes server data goes through `IWinoRequestDelegator`; a ViewModel never writes the database. The pipeline is described in [Wino.Core](../Wino.Core/AGENTS.md).
- **CsWinRT attributes are for Windows marshalling only.** The project has a second, Windows-only target that defines `WINRT_EXPOSED`. Add `[GeneratedWinRTExposedType]` or `[GeneratedBindableCustomProperty]` under that symbol only for a type that a WinUI items source or a runtime `{Binding}` receives.
- **Register shared ViewModels only in `RegisterWinoViewModels`** (`WinoViewModelRegistration.cs`). Every head calls it.

## The contract with a view

Every head drives a ViewModel the same way, so write the ViewModel to this contract and it works in all four:

1. The page resolves the ViewModel from the container and assigns `Dispatcher`.
2. `OnNavigatedTo(mode, parameter)` runs. The base class registers messenger recipients here.
3. `OnPageLoaded()` runs when the view is on screen.
4. The view observes `[ObservableProperty]` properties and observable collections, and invokes commands.
5. `OnNavigatedFrom(mode, parameter)` runs. The base class unregisters recipients.

How each framework binds to this is in the head's file: [WinUI](../Wino.Mail.WinUI/AGENTS.md), [macOS](../Wino.Mail.MacOS/AGENTS.md), [iOS](../Wino.Mail.UIKit/AGENTS.md), [Android](../Wino.Mail.Android/AGENTS.md). When you add or rename a property or command that a view uses, update the view in every head, including the ones you cannot build, and report those as pending.

## Always

- Declare state as a public partial property with `[ObservableProperty]`. Do not annotate a private field.
- Register messenger handlers in `RegisterRecipients()` and remove them in `UnregisterRecipients()`.
- Recipients run on background threads. Change UI state, collections and navigation inside `ExecuteUIThread(...)` or `ExecuteUIThreadAsync(...)`. Code after `ConfigureAwait(false)` is background-thread code too.
- Give a collection that reaches an items source a concrete static type (`List<T>`, `ObservableCollection<T>`, an array). `WINOAOT001` fails the build otherwise.
- Take services through the constructor as interfaces. New code uses the injected `ISynchronizationManager` and `IMessenger`; the static `SynchronizationManager.Instance` and `WeakReferenceMessenger.Default` are being removed.
- Take every user-visible string from `Translator`.
- Tie a command's availability to state with `CanExecute` and `[NotifyCanExecuteChangedFor]`.

## Running a change

Build the project. It has one portable target off Windows and two targets on Windows:

```powershell
dotnet build src/Wino.Mail.ViewModels/Wino.Mail.ViewModels.csproj
```

A ViewModel cannot be created from a script yet: it needs about twenty services that only an app registers, including the dispatcher, navigation and dialogs. Until a portable host exists, check behavior in a running app. On Windows that is the Debug app with `winapp ui` against leased lab accounts. On a machine with no app to run, report the build result and "behavior check pending" with the ViewModel and command you changed.

Logic that needs no ViewModel instance, such as a static helper or a model, runs from a single-file script; see "Running backend code without an app" in the [root guidance](../../AGENTS.md).
