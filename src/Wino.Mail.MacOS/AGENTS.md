# Wino.Mail.MacOS

The macOS app: AppKit views written in C# over the shared services and ViewModels. Debug runs on CoreCLR; Release is published with Native AOT, and a trim or AOT warning in the app fails the publish. This file is binding for `src/Wino.Mail.MacOS` and `src/Wino.Platform.MacOS`.

## Read first

| When you | Read |
| --- | --- |
| Build, sign, debug or check the app | [macOS development](../../docs/harness/development-macos.md) |
| Add a screen or a visual pattern | [macOS design decisions](../../docs/macos-design-decisions.md) |
| Need a rule that only the Windows app has | [shared-logic roadmap](../../docs/macos-shared-logic-roadmap.md) |
| Touch sign-in or an OAuth client | "Accounts, OAuth clients and signing" in the [root guidance](../../AGENTS.md) |
| Touch the Keychain, StoreKit, notifications or paths | [Wino.Platform.Apple](../Wino.Platform.Apple/AGENTS.md) |

## Hard stops

- Apple: iOS and macOS share App ID `com.winomail.macos` (team `4VB7YWRAQ9`) with Sign in with Apple enabled. The iOS entitlements files and `Wino.Mail.MacOS/Entitlements.AppStore.plist` carry `com.apple.developer.applesignin`; macOS development builds get it through `CustomEntitlements` when a profile signs the build.
- Developer ID profiles cannot carry Sign in with Apple. `build-macos-release.sh` passes `-p:WinoAppleSignIn=false`; never remove that or add the entitlement to DMG builds, or macOS refuses to launch the app. DMG users sign in with Apple through the API's browser flow. The Mac csproj defines `WINO_APPLE_SIGNIN` under exactly the entitlement condition; `Composition` registers the native `AppleNativeSignInProvider` with it, and without it the shared `WinoAccountExternalSignInService` falls back to the `ASWebAuthenticationSession` browser flow.
- Provisioning profiles are selected by name ("Wino Mail iOS Development", "Wino Mail iOS App Store", "Wino Mail macOS App Store", "Wino Mail macOS Developer ID", "Wino Mail macOS Development"). Recreating a profile must keep its exact name. Changing App ID capabilities invalidates all of them.
- Build Apple targets with `~/.dotnet/dotnet` (it has the ios/macos/android workloads; `/usr/local/share/dotnet` does not). Code signing fails over SSH (`errSecInternalComponent`: the login keychain is locked per SSH session), so signed builds run in a local Terminal.
- Keep `Entitlements.plist` and `Entitlements.AppStore.plist` in step. After touching either Apple sign-in path, also build Debug with `-p:WinoAppleSignIn=false`.
- Keep the user's app data, Keychain items and bundle id. Inspect and repair data through the app, inside the sandbox.

## Mail renderer

Mail is read, printed and composed with the native mail renderer (`Wino.Renderer`), not with WebKit. `build/Wino.Renderer.Apple.props` references the `WinoMail.Renderer.macOS` package: the engine, its Core Graphics and Core Text backend (`Wino.Renderer.Platform.Apple`) and the AppKit view (`Wino.Renderer.AppKit`). `WINO_RENDERER_ROOT` builds against a renderer checkout instead.

- The glue shared with the other heads is in [src/Wino.Mail.Rendering](../Wino.Mail.Rendering/AGENTS.md) (`MailEngineHost`, `EngineMailMessageReader`, `EngineMailEditorSession`, `EngineReaderPresentation`, print documents, spell-check languages); the Apple parts (backend factory, PDF export, memory pressure) are in `src/Wino.Mail.Rendering.Apple`. Change them there.
- [Composition.cs](Composition.cs) registers the one backend (`AppKitBackend.Create()`) and calls `AddMailRendering()`. Never create a second engine.
- The engine's `MailView` (an `NSView` over the shared `MailViewController`) is created in code: the reader in `MailRenderingPageViewController`, the composer and the signature, template and event-notes editors through `Views/Mail/Compose/MacEngineEditor` and `Views/Dialogs/WinoRichTextEditorView`. `Infrastructure/MacEngineMailPrintPresenter` prints through the engine's print panel sheet and exports PDF.
- Links, remote content, tracking parameters, find, zoom, the caret, IME and spelling are the engine's. Keep this head to wiring and AppKit chrome; an engine fault is fixed in the renderer repository.
- A closing window clears its content controller with `ClearContentViewController()` (the binding refuses null) before the page is disposed; otherwise the page and the engine view stay alive.
- Debug bridge: `compose-type`, `compose-key`, `compose-ime`, `compose-body`, `compose-discard`, `reader-pointer X Y [click]`, `reader-print`, `reader-pdf`, `eml-open`, `eml-close`, `gc` and `live` drive and measure the renderer in the running app.

## How a view consumes a ViewModel

A page is a `WinoViewController<TViewModel>` ([Infrastructure/WinoViewController.cs](Infrastructure/WinoViewController.cs)). The base class owns the contract with the shared ViewModel, so a page only declares bindings:

- The controller and its ViewModel come from `PageScopes.Resolve`, one scope per page. Register both in [Infrastructure/MacViewModelRegistration.cs](Infrastructure/MacViewModelRegistration.cs).
- The base assigns `ViewModel.Dispatcher`, calls `OnNavigatedTo` and `OnPageLoaded` on activation, `OnNavigatedFrom` on deactivation, and clears the dispatcher on release.
- Bindings go into `Bindings` (a `BindingScope`) so they are disposed with the page. The binding types are in `controls/Wino.Presentation.Core/Bindings`.

```csharp
protected override Task InitializeAsync(NavigationMode mode, object? parameter)
{
    Bindings.Own(new PropertyBinding<IdlePageViewModel, string>(
        ViewModel, nameof(ViewModel.AccountStateMessage), vm => vm.AccountStateMessage,
        _ => Apply(), Dispatcher, ReportError));
    return base.InitializeAsync(mode, parameter);
}
```

A page reads state and invokes commands on its ViewModel. When a page needs data the ViewModel does not expose, add it to the shared ViewModel; about 25 pages still resolve a service directly, and new code does not add to them.

## Always

- Put a business rule in the shared library that already owns similar code. The page keeps presentation only.
- Keep Mac-specific choices (the separate Settings window, sheets, the menu bar). They are deliberate and are not duplication to remove.
- Register every new service in [Composition.cs](Composition.cs); AppKit-only services go to `Wino.Platform.MacOS`, services both Apple apps need go to `Wino.Platform.Apple`.

## Check your change

- **On a Mac:** native Debug builds for arm64 and x64, then a manual check in the running app. Commands are in the macOS guide.
- **On any other machine:** this project does not build there. Build the shared projects you touched, follow the pattern above for the view code, and report "macOS build and manual check pending" with the list of changed files.

A build does not prove UI behavior. Do not notarize or publish to validate a change.

## Performance

- Reusable cells subscribe to style and icon changes through the weak `*Watchers`, never through the static `WinoStyle` or `WinoIcons` events. A static subscription keeps every pooled cell alive.
- Keep one `InVisibleRect` tracking area per view.
- In a cell, attach `WinoCheckbox` handlers with `WinoControlEvents.OnActivated`; a capturing lambda passed to `WinoCheckbox.Create` leaks the cell.
