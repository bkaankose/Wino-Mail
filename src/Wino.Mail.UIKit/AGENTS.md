# Wino.Mail.UIKit

The iPhone and iPad app: UIKit views written in C# over the shared services and ViewModels. It runs on Mono; Native AOT stays off. This file is binding for `src/Wino.Mail.UIKit`, `src/Wino.Mail.UIKit.Phone` (the shell for iPhone and iPad), the head `src/Wino.Mail.iOS` and `src/Wino.Platform.iOS`.

## Read first

| When you | Read |
| --- | --- |
| Build, sign, use the Simulator or check the app | [iOS development](../../docs/harness/development-ios.md) |
| Add a screen or a visual pattern | [iOS design decisions](../../docs/ios-design-decisions.md) |
| Touch sign-in or an OAuth client | "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md) |
| Touch signing, entitlements or provisioning profiles | Hard stops in [Wino.Mail.MacOS](../Wino.Mail.MacOS/AGENTS.md); both Apple apps share one App ID |
| Touch the Keychain, StoreKit, notifications or paths | [Wino.Platform.Apple](../Wino.Platform.Apple/AGENTS.md) |

## Hard stops

- The bundle id `com.winomail.macos` is shared with the Mac app. Keep it, and keep the user's app data and Keychain items.
- Pass `-p:WinoTargetPlatform=iOS` on every build. The host never selects iOS.
- Use the unversioned `net10.0-ios` target framework in every iOS project. Minimum OS is 17.0.
- The relay's APNs flag must match `aps-environment`.
- Debug bridge commands are registered only inside `#if DEBUG`.
- Never build a `UIButton` subclass with `base(UIButtonType.System)`.

## Mail renderer

Mail is read, printed and composed with the native mail renderer (`Wino.Renderer`), not with WebKit. `build/Wino.Renderer.Apple.props` references the engine, its Core Graphics and Core Text backend and the UIKit view (`Wino.Renderer.UIKit`) from the checkout `WINO_RENDERER_ROOT` names; set it before every build.

- The glue shared with the other heads is in [src/Wino.Mail.Rendering](../Wino.Mail.Rendering/AGENTS.md); the Apple parts (backend factory, PDF export, memory pressure) are in `src/Wino.Mail.Rendering.Apple`. `RegisterUIKitServices` registers the one backend (`UIKitBackend.Create()`) and calls `AddMailRendering()`; the head's `Composition` starts `MailEngineMemoryPressure`. Never create a second engine.
- The engine's `MailView` (a `UIView` over the shared `MailViewController`) is created in code. The reader and the event description set `SizesToContent`: the page's scroll view scrolls the mail with its header, and the view draws only the part on screen. The composer, the signature and template editors and the event notes go through `Views/Mail/Compose/UIKitEngineEditor`; the format bar is the view's `InputAccessory`. `Infrastructure/UIKitEngineMailPrintPresenter` presents the print sheet and exports PDF.
- Links, remote content, tracking parameters, find, zoom, the caret, marked text, spelling and Dynamic Type for mail text are the engine's. Keep this head to wiring and UIKit chrome; an engine fault is fixed in the renderer repository.
- Debug bridge: `reader-theme`, `reader-touch X Y [long]`, `reader-print`, `reader-pdf`, `compose-type`, `compose-ime`, `compose-key`, `compose-image`, `compose-body` and `compose-discard` drive the renderer in the running app.

## How a view consumes a ViewModel

A page is a `WinoViewController<TViewModel>` ([Infrastructure/WinoViewController.cs](Infrastructure/WinoViewController.cs)). The base class owns the contract with the shared ViewModel, so a page only declares bindings:

- The controller and its ViewModel come from `PageScopes.Resolve`, one scope per page. Register pages in [UIKitViewRegistration.cs](UIKitViewRegistration.cs); phone routes are in `src/Wino.Mail.UIKit.Phone/PhonePageRegistry*.cs`.
- The base assigns `ViewModel.Dispatcher`, calls `OnNavigatedTo` and `OnPageLoaded` on activation and `OnNavigatedFrom` on deactivation. It releases the page by itself when the page is popped or dismissed.
- `Bind(propertyName, read, apply)` observes one ViewModel property and disposes with the page.

```csharp
protected override Task InitializeAsync(NavigationMode mode, object? parameter)
{
    Bind(nameof(ViewModel.IsMailEmptyStateVisible), vm => vm.IsMailEmptyStateVisible, _ => Apply());
    Bind(nameof(ViewModel.AccountStateMessage), vm => vm.AccountStateMessage, _ => Apply());
    return base.InitializeAsync(mode, parameter);
}
```

A page reads state and invokes commands on its ViewModel. When a page needs data the ViewModel does not expose, add it to the shared ViewModel; about 38 pages still resolve a service directly, and new code does not add to them.

## Always

- Put a business rule in shared code, never in an Apple project. If the rule exists only in another head, move it to shared code first.
- Keep the UIKit libraries the twins of the AppKit ones: same member names and feature folders. Logic both need goes to `controls/Wino.Mail.Controls.Core` or `controls/Wino.Presentation.Core`.
- Register every new service in the head's [Composition.cs](../Wino.Mail.iOS/Composition.cs) or the registration method it delegates to. UIKit-only services go to `Wino.Platform.iOS`.

## Check your change

- **On an Apple Silicon Mac:** the Debug Simulator build, then manual checks on an iPhone and an iPad simulator. Use one simulator per task, addressed by UDID. Commands are in the iOS guide.
- **On any other machine:** this project does not build there. Build the shared projects you touched, follow the pattern above for the view code, and report "iOS Simulator build and manual checks pending" with the list of changed files.

A build does not prove UI behavior. Do not upload to TestFlight to validate a change, and never paste a lab password into a report.
