# macOS development

This is the authoritative guide for the native AppKit application in `src/Wino.Mail.MacOS` and its solution `WinoMail.MacOS.slnx`.
The app uses the shared .NET services, ViewModels, translations, and editor engine through explicit AppKit adapters. It does not use Uno, MAUI, Catalyst, or XAML.

Shared rules for every platform are in [AGENTS.md](../../AGENTS.md) and the [implementation rules](implementation-rules.md).
Presentation decisions are in [macOS design decisions](../macos-design-decisions.md). The plan for moving duplicated Mac logic into shared code is the [macOS shared-logic roadmap](../macos-shared-logic-roadmap.md).

## Requirements

- A Mac with Apple Silicon for F5 debugging. Intel Macs can run the x64 and universal builds.
- The .NET SDK selected by `global.json`, with the matching `macos` and `ios` workloads: `dotnet workload install macos ios`. The Apple shared libraries (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `Wino.Editor.WebKit`) also target `net10.0-ios`, and builds without a RID, such as the solution build, restore that target too.
- The Xcode version that the installed workload requires, selected with `xcode-select`.
- VS Code with Microsoft's C# extension, running on the Mac (locally or as a remote extension host on the Mac).

Last verified combination (2026-10-07): macOS 26.7 on Apple Silicon, .NET SDK 10.0.401 (runtime 10.0.12), macOS workload 27.0.10722, Xcode 27.0 (27A266a).
After an SDK, workload, or Xcode update, rebuild arm64 and x64 and repeat the F5 breakpoint check below.

## Platform selection

Shared projects choose their target frameworks from `WinoTargetPlatform`. On a Mac host the build resolves `MacOS` automatically, so shared projects build only their portable `net10.0` target and no Windows runtime defaults apply.
Explicit `-p:WinoTargetPlatform=MacOS` still wins and is the documented form for scripts and tasks.
iOS builds on the same host pass `-p:WinoTargetPlatform=iOS` explicitly; the host never selects it. Its policy is in `build/Wino.iOS.targets`, and the [iOS development](development-ios.md) guide covers the iOS app.
The Apple shared libraries multi-target `net10.0-macos;net10.0-ios` and narrow their targets by RID: an `osx-*` RID builds only `net10.0-macos`, because restoring the iOS target for an `osx-*` RID fails with NU1102.

Pass `WinoTargetRuntimeIdentifier` globally with the native RID for single-architecture builds. The SDK clears `RuntimeIdentifier` on portable reference builds, and this selector keeps the ML project's restore and compile consistent:

- Apple Silicon (`osx-arm64`) uses the ONNX-backed Magika classifier.
- Intel (`osx-x64`) uses the existing unavailable ML implementation. Physical Intel runtime behavior is not verified.
- Universal release builds pass `RuntimeIdentifiers=osx-arm64;osx-x64` and no single-architecture selector; keep both halves when changing release inputs.

## Build

```sh
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-x64 -p:WinoTargetRuntimeIdentifier=osx-x64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
dotnet build WinoMail.MacOS.slnx -c Debug -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
```

Output is `src/Wino.Mail.MacOS/bin/Debug/net10.0-macos/<RID>/Wino Mail.app`. The bundle's native launcher is `Contents/MacOS/Wino.Mail.MacOS`.
Debug and Release use CoreCLR (`UseMonoRuntime=false`); the bundle contains `libcoreclr.dylib`. The AppKit head is exempt from the shared Native AOT gate (`build/Wino.MacOS.targets`); portable libraries keep their Release AOT and trimming analyzers.

## Identity, signing, and sandbox

| Item | Value |
| --- | --- |
| Bundle identifier | `com.winomail.macos` (`ApplicationId` in the project, `CFBundleIdentifier` in `Info.plist`) |
| Team | `4VB7YWRAQ9` |
| Debug signing | `Apple Development` identity with the **Wino Mail macOS Development** profile. Pass `-p:CodesignKey=- -p:CodesignProvision=` to sign ad hoc; an ad hoc build cannot use the keychain, so it cannot keep accounts signed in. |
| DMG release signing | **Developer ID Application** with the **Wino Mail macOS Developer ID** profile, hardened runtime ([macOS release guide](../releases-macos.md)) |
| Keychain | `AppleKeychainStore` uses the data protection keychain. Access follows the signed `com.apple.application-identifier`, which only a provisioning profile provides, so new builds and updates never ask for keychain access. |
| App Store signing | **Apple Distribution** with the App Store profile and `Entitlements.AppStore.plist` |

`Entitlements.plist` enables App Sandbox, network client, network server (loopback listeners for system-browser OAuth callbacks), user-selected file read/write, and JIT. It also grants Sparkle's installer mach-lookup exception for DMG builds. `Entitlements.AppStore.plist` is the same set without Sparkle's exception; keep the two in step.
Debug builds read Sparkle's feed from `http://127.0.0.1:8089/appcast.xml` (`Sparkle.Debug.plist`), never the production feed, so they are not offered the published DMG.

Live updates (WinoRelay) on the Mac use the relay socket only, while the app runs; the Mac registers no APNs token and a quit app receives nothing (owner decision, 2026-10-17). The `com.apple.developer.aps-environment` entitlement is still present in the signed builds because the profiles carry the Push Notifications capability ("Wino Mail macOS Development" development, "Wino Mail macOS App Store" and "Wino Mail macOS Developer ID" production; the DMG script passes `-p:WinoApsEnvironment=production`), and a profile-signed app must carry what its profile allows. Ad hoc builds (`-p:CodesignKey=- -p:CodesignProvision=`) have no profile and no entitlement, which changes nothing for the relay.

Codesign needs the login keychain. SSH sessions cannot reach it (`errSecInternalComponent`); build signed bundles from Terminal or a `screen` session started in Terminal, or sign ad hoc.

Wino Account Sign in with Apple follows the `com.apple.developer.applesignin` entitlement. The project defines `WINO_APPLE_SIGNIN` under the same condition that adds the entitlement (App Store distribution, or a profile-signed build with `WinoAppleSignIn` true, the default); `Composition` then registers the native `AppleNativeSignInService` (`ASAuthorizationController`). Ad hoc builds and DMG builds (`-p:WinoAppleSignIn=false`) have no entitlement and register the API browser flow instead (`MacWebAuthenticationBroker`, an `ASWebAuthenticationSession` that catches the `wino://auth-complete` redirect itself). Build the Debug app once with `-p:WinoAppleSignIn=false` after touching either path so both compile. Google sign-in for Wino Accounts is native in every Mac build: the same web authentication session shows Google's consent page for the Wino Accounts iOS client (`GoogleWebSessionSignInProvider`, PKCE, redirect `com.googleusercontent.apps.<client>:/oauthredirect`) and the code is exchanged for the ID token the API verifies.

## Storage, Keychain, and activation

- Application data: `~/Library/Containers/com.winomail.macos/Data/Library/Application Support/com.winomail.macos` (database, preferences, logs, MIME files). `ApplePaths` (Wino.Platform.Apple) resolves it through the sandbox container.
- Temporary files: `~/Library/Containers/com.winomail.macos/Data/tmp/com.winomail.macos`.
- Credentials: generic Keychain passwords with the service name `com.winomail.macos.v1.<scope>` (`AppleKeychainStore` in Wino.Platform.Apple; the identity comes from `Composition.cs`).
- Activation (`Info.plist`): `mailto`, `wino`, and `webcal`/`webcals` URL schemes; `.ics`, vCard, and `.eml` files as an alternate handler; files dropped on the Dock icon and the Finder service become attachments of a new message.

Preserve application data, Keychain items, and the bundle identifier. Do not bypass the sandbox to inspect or repair data.

Historical identities: experimental builds before 7 October 2026 used `com.winomail.desktop`, and their data remains under `~/Library/Containers/com.winomail.desktop`. Evidence that names that identity, such as the [AppKit foundation validation](appkit-foundation-validation.md), is historical. The current app does not migrate it.

## VS Code F5

1. Open [WinoMail.MacOS.code-workspace](../../WinoMail.MacOS.code-workspace). It selects `WinoMail.MacOS.slnx` and adds the Mac tasks and launch configuration.
2. Select **Debug Wino Mail (macOS, arm64)** and press **F5**.

**Ctrl+Shift+B** runs **Build Wino Mail (macOS, arm64)**, a plain `dotnet build` that does not need the debugger. **Build Wino Mail (macOS, x64)** builds the Intel bundle.
F5 runs **Prepare and build Wino Mail (macOS, arm64)**: it first runs [setup-macos-debugger.sh](../../scripts/maintenance/macos/setup-macos-debugger.sh), then the same build.
The repository folder's own `.vscode` configuration is the Windows workflow. VS Code also loads it in the Mac workspace, so its Windows entries appear there; if **Ctrl+Shift+B** offers both default build tasks, choose the macOS one.

The C# debugger launches the bundle's native executable under `Wino Mail.app/Contents/MacOS/Wino.Mail.MacOS`. Both the application environment and `pipeTransport.pipeEnv` set `TMPDIR` to `~/Library/Containers/com.winomail.macos/Data/tmp`. Setting only `launch.env` does not change the debugger's environment. The supported [C# pipe transport](https://github.com/dotnet/vscode-csharp/blob/main/docs/debugger/Attaching-to-remote-processes.md) uses `/bin/zsh -c` to start the prepared debugger on the same machine.

### Debugger compatibility on macOS 26

The C# extension tested here is `2.160.4`, with debugger `18.12.10903.2`. Its bundled diagnostic shim `9.0.12.27201` did not establish the sandboxed launch handshake: the app ran, while source breakpoints remained unprocessed. Microsoft tracks the macOS 26 semaphore restriction in [runtime issue 116545](https://github.com/dotnet/runtime/issues/116545); current shims use startup FIFOs.

The setup script discovers the installed C# extension through `code --locate-extension ms-dotnettools.csharp`, copies its matching debugger into `~/.local/share/wino-vsdbg`, and replaces only the copied `libdbgshim.dylib` with Microsoft's [Microsoft.Diagnostics.DbgShim.osx-arm64 10.0.745401](https://www.nuget.org/packages/Microsoft.Diagnostics.DbgShim.osx-arm64/10.0.745401) package (native version `10.0.14.45401`). It checks the package SHA-256 `adcac4c69e2e5a29f6dc9df8a8ba589011a183bf505882cc37d137f63545375e` and installed shim checksum. This is a versioned compatibility workaround, not a debugger distribution supplied by the C# extension. No binaries are checked into the repository.

The installed extension remains untouched. Setup repeats after an extension/debugger version change or a missing/corrupt prepared shim; unchanged tooling requires no download. Do not substitute an independently downloaded `vsdbg`: the tested `getvsdbgsh -v latest` returned `18.10.10709.3`, which rejected the `18.12` adapter's source breakpoint requests with `Incorrect breakpoint request format.` Extension updates require another breakpoint check; the shim pin is not a guarantee of compatibility with future adapters.

### Debugger repair

Run setup manually to repair tooling:

```sh
bash scripts/maintenance/macos/setup-macos-debugger.sh
```

The first setup needs access to nuget.org. If the VS Code CLI is outside `PATH` and the standard `/Applications/Visual Studio Code.app` location, set `WINO_VSCODE_CLI` to its executable path. Setup preserves replaced debugger directories as `~/.local/share/wino-vsdbg.previous.<timestamp>`. To roll back, stop debugging, move the current prepared directory aside and restore the previous one; automatic F5 setup will prepare the currently configured shim again, so a deliberate rollback also requires reverting the setup pin/configuration. The app's sandbox, signing entitlements, identity, account database and Keychain references are unchanged.

### Mac F5 acceptance

On 2026-10-07, the production arm64 app hit a source breakpoint at `Program.cs:9`, stepped to line 10, then hit `AppDelegate.StartAsync` at `AppDelegate.cs:38` with locals and managed stack frames visible. Continuing opened the saved account's native shell and Inbox. A final run through the Mac workspace repeated the `Program.cs:9` breakpoint, stepping and shell continuation with exact source checks and automatic setup. Evidence is recorded locally on the Mac in `artifacts/appkit-macos-f5-acceptance.txt`. These checks establish debugger startup and continuation; fresh authentication, Remote SSH debugging, full UI parity and manual acceptance remain separate work.

## Control lifetime and performance rules

- Views that AppKit creates many times (table and outline cells, row views, calendar tiles, chips, zones) never subscribe to the static C# events `WinoStyle.AccentChanged`, `WinoStyle.BackdropChanged` or `WinoIcons.StyleChanged`. AppKit does not call `Dispose` on pooled cells, so a static event roots them for the life of the app. Register on the weak counterparts instead (`WinoStyle.AccentWatchers`, `WinoStyle.BackdropWatchers`, `WinoIcons.StyleWatchers`) with a static lambda that takes the view as its parameter, and remove in `Dispose`. Pages with a `Bindings` scope may use the static events through `Bindings.Own`.
- Cache colours, fonts and attributed strings that do not change per row. Dynamic `NSColor`s resolve per appearance at draw time, so one static instance is correct in light and dark mode.
- In `Configure`, `Apply` and `Bind`, set only what changed: fonts re-measure fields, `SetCustomSpacing` and constraint constants re-solve the stack, `NeedsDisplay` repaints the layer.
- Add one `InVisibleRect` tracking area per view and keep it; do not rebuild it in `UpdateTrackingAreas`.
- Timers and observers that reference a view start in `ViewDidMoveToWindow` when the window is set and stop when it is cleared.
- Coalesce collection bursts into one reload per run-loop turn with `BeginInvokeOnMainThread` and a flag; `IDispatcher.ExecuteOnUIThread` runs inline on the main thread and does not coalesce.
- A framework control (`NSButton`, `NSMenuItem`, `NSTextField`) with a managed event handler keeps its managed wrapper rooted while its native object lives. A handler that captures the owning cell or page forms a cycle across the native boundary that the collector cannot break. In reusable cells attach handlers with `WinoControlEvents.OnActivated(control, this, static owner => ...)`; never pass a capturing lambda to `WinoCheckbox.Create` from a cell. Menus owned by a view are released with `WinoMenus.DisposeTree`.
- Page controllers resolve from DI through `PageScopes.Resolve`, which gives each page its own scope; a transient `IDisposable` resolved from the root provider is tracked until the app exits.
- `WinoViewController.Dispose` disposes child controllers, removes the page view from its superview and disposes every wrapper in the tree (`ViewTreeDisposal`), including table and pop-up menus. A page that keeps extra native objects outside its view tree (menus shown with `PopUpMenu`, windows, timers) releases them in its own `Dispose` override.
- Check for leaks with the debug bridge: `live` prints the tracked view and page instances that survive a full collection (the second number is instances whose native peer is already disposed), `chain <Type>` prints their superview chains, and `scripts/maintenance/macos/collect-gcdump.sh` collects a GC heap graph (the sandbox truncates the diagnostics socket name, so the script proxies it; `dotnet-dump` cannot run inside the sandbox). Launch the binary directly for a diagnostics socket; an `open`-launched instance has none.

## Verification

| Change | Required evidence |
| --- | --- |
| AppKit head, AppKit controls, or Mac platform services | Native Debug arm64 and x64 builds, plus manual checks on the Mac |
| Apple shared layer (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `Wino.Editor.WebKit`) | The native Debug builds above and the iOS Debug Simulator build in [iOS development](development-ios.md) |
| Shared code used by the Mac | The Mac builds above and the Windows checks in [AGENTS.md](../../AGENTS.md) |
| Signing, entitlements, bundle identity, or release inputs | Native Release builds for arm64 and x64, `codesign --verify --deep --strict`, effective entitlements and bundle identifier; universal architecture selection when release inputs change |
| Debugger tooling or workspace | The F5 acceptance steps above on Apple Silicon |

Do not notarize or publish to validate development changes. A build does not prove UI behavior.
The local Docker lab currently runs on Windows only. Mac manual checks use the Mac app directly; do not reuse lab credentials generated for Windows on the Mac.

## Release

See the [macOS release guide](../releases-macos.md) for the DMG and Mac App Store packages. The scripts live under `scripts/release/macos`.
