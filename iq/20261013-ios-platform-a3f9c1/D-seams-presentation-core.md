# D: shared seams (T6) and Wino.Presentation.Core (T7)

Worktree: `D:\Wino-Mail\.claude\worktrees\agent-a5e879402a516dda0` (branch `worktree-agent-a5e879402a516dda0`). Nothing is committed.
Mac mirror: `~/Desktop/wt-D`.
This report file is also in the worktree as `iq/20261013-ios-platform-a3f9c1/D-seams-presentation-core.md`. That copy is untracked: delete it, or leave it out when you apply the diff.

## T6 seams

### 1. `IGoogleAuthorizationCodeReceiver`
- The Gmail authenticator lives in `Wino.Authentication`, so the seam is there too.
  - New `src/Wino.Authentication/IGoogleAuthorizationCodeReceiver.cs` has `Task<GoogleAuthorizationCode> ReceiveCodeAsync(Func<Uri, string, Uri> authorizationUriFactory, CancellationToken)`. The receiver chooses the redirect URI and state, builds the authorization URI through the factory, and returns the code, redirect URI and PKCE verifier.
  - `GoogleAuthorizationCode` is now a public record.
- New `GoogleAuthorizationPkce` (public static) holds the state, verifier and S256 challenge helpers. They were moved unchanged out of the receiver so that an iOS custom-scheme receiver can reuse them.
- `WinoGmailCodeReceiver` (the loopback `HttpListener` receiver) is now public and implements the interface. Its behaviour is unchanged.
- `GmailAuthenticator` has a new constructor `(IAuthenticatorConfig, IGoogleAuthorizationCodeReceiver, IGoogleTokenStore)`. The old 4-argument constructor is kept: it builds the loopback receiver, and the existing tests use it.
- Registrations:
  - `Wino.Core` `RegisterCoreServices`: `TryAddTransient<IGoogleAuthorizationCodeReceiver>` registers the loopback receiver by default.
  - `IGmailAuthenticator` is now registered through an explicit factory instead of reflection-based constructor selection.
  - The new public extension `AddLoopbackGoogleAuthorizationCodeReceiver()` uses `Replace`, so a head registers its own receiver after `RegisterCoreServices`.
- Windows: `src/Wino.Mail.WinUI/CoreUWPContainerSetup.cs` gets one added line, `global::Wino.Core.CoreContainerSetup.AddLoopbackGoogleAuthorizationCodeReceiver(services);`. The call is fully qualified so no `using` had to be added. The rest of the file is untouched.
- Mac: `src/Wino.Mail.MacOS/Composition.cs` gets `services.AddLoopbackGoogleAuthorizationCodeReceiver();`, placed after the browser-auth presenter registration.
- iOS (later): register a custom-scheme `IGoogleAuthorizationCodeReceiver` after `RegisterCoreServices`.

### 2. `IApplicationRuntime.SynchronizeOnceAsync(CancellationToken)`
- `ApplicationRuntime.SynchronizeOnceAsync` initializes the runtime if needed, then runs one tracked pass on the thread pool. The pass:
  - runs mail inbox sync for all accounts (`ExecuteAutoSynchronizationAsync`, which keeps the inbox counter and the full sync every 20 inbox syncs);
  - runs calendar metadata sync (`ExecuteCalendarAutoSynchronizationAsync`);
  - runs task and contact sync only when their six-hour window is due.
- The window check was extracted from the loop into `RunRareModeIfDueAsync`. The loop now calls it, with the same behaviour as before.
- The pass never creates timers, never changes state to Running and never starts loops.
- The caller's token is linked with the runtime lifetime. If one mode throws an exception other than cancellation, the error is logged and the next mode still runs; cancellation still propagates to the caller. `StopAsync` waits for the pass because it is tracked.
- Start and stop behaviour is unchanged.
- New tests in `tests/Wino.Core.Tests/ApplicationRuntimeTests.cs`:
  - `SynchronizeOnce_RunsOneBoundedPassWithoutStartingLoops` checks one InboxOnly mail sync, one CalendarMetadata sync, contacts synced because they are due, the task window stamped but not synced, state Initialized, and 0 periodic timers.
  - `SynchronizeOnce_CanceledToken_StopsThePass`.

### 3. `Microsoft.Identity.Client.Broker`
- `src/Wino.Core/Wino.Core.csproj`: the PackageReference is now conditional on `'$(WinoTargetPlatform)' == 'Windows'`.
- No compile define was needed. `Wino.Core` has no code that uses the Broker; the only use is `src/Wino.Platform.Windows/WindowsOutlookAuthenticationHost.cs`, and that project has its own direct PackageReference. `WINO_WINDOWS` was therefore not introduced.
- Verified on the Mac:
  - In `src/Wino.Core/obj/project.assets.json`, the Broker appears only under `centralPackageVersions`. There are 0 `Microsoft.Identity.Client.Broker/` library entries.
  - The Mac tree contains 0 `Microsoft.Identity.Client.Broker.dll` files.
  - The Mac build passes.

### 4. Signature platform name
- `IAppMetadataService` has a new `string PlatformDisplayName { get; }`.
  - Windows (`NativeAppService`) returns `"Windows"`.
  - Mac (`MacAppMetadataService`) takes a new optional constructor parameter `platformDisplayName = "Mac"`.
- `SignatureService` now takes `IAppMetadataService` and uses it instead of `OperatingSystem.IsMacOS()`.
- Updated the test that constructs `SignatureService` directly (`WinoAccountDataSyncServiceTests`).

## T7: `controls/Wino.Presentation.Core`
- New `Wino.Presentation.Core.csproj`: `net10.0`, platforms x86/x64/arm64, nullable on, references `Wino.Core.Domain` and `CommunityToolkit.Mvvm`. It inherits the repo AOT and trimming Release gate.
- Moved with `git mv`:
  - `controls/Wino.Presentation.AppKit/Bindings.cs` → `controls/Wino.Presentation.Core/Bindings/Bindings.cs`. The namespace is now `Wino.Presentation.Bindings`; the code is unchanged.
  - `src/Wino.Mail.MacOS/Views/Settings/AnyPropertyBinding.cs` → `controls/Wino.Presentation.Core/Bindings/AnyPropertyBinding.cs`. It is now public, in `Wino.Presentation.Bindings`.
    - The `NSThread.IsMain` branch was removed in favour of `IDispatcher.ExecuteOnUIThread`. `AppKitDispatcher` already runs inline on the main thread, so behaviour is identical.
- New neutral tokens in namespace `Wino.Presentation`:
  - `WinoTokens`: spacing, radius, size and zone constants; `AvatarPalette` as a read-only list of `#RRGGBB` strings; and `AvatarPaletteIndex(key)`, the moved hash logic.
  - `WinoRgba`: `FromRgb`, `FromHexString`, `Parse`.
  - `WinoThemeSurface`: the enum moved from `WinoThemeSurfaces.cs`.
- AppKit `WinoStyle` keeps every public name.
  - The constants forward to `WinoTokens`.
  - `Hex`, `FromHexString`, `AvatarPalette` and `AvatarColor` go through `WinoRgba` and `WinoTokens`.
  - A new `ToColor(WinoRgba)` helper was added.
  - The NSColor and NSFont members stay in AppKit.
- `Wino.Presentation.AppKit.csproj` now references the new project.
- `using` changes:
  - `using Wino.Presentation.Bindings;` was added to 45 files: 43 in `src/Wino.Mail.MacOS`, `controls/Wino.Mail.Controls.AppKit/Settings/SettingsBinder.cs`, and the tests.
  - `using Wino.Presentation;` was added to 4 files that use `WinoThemeSurface`: `WinoCalendarSurfaceView.cs`, `WinoMailTableRowView.cs`, `MacWinoThemeService.cs` and `ThemesDebug.cs`.
  - No other edits were made in those folders.
- `tests/Wino.Mail.Controls.Tests`: the project now references the new project. The linked `Bindings.cs` and `IDispatcher.cs` were removed; `IDispatcher` now comes through Domain. New `PresentationTokensTests.cs` adds 7 cases.
- Solutions: the new project was added to `WinoMail.MacOS.slnx` (under `/MacOS/`) and to `WinoMail.slnx` (under `/controls/`, with platform mappings).
- `controls/AGENTS.md`: the project list now names `Wino.Presentation.Core`. This is two lines.

## Verification
The Windows PC restarted during the first run, which left zero-filled `obj` files (CS1056 `'\0'` in the CsWinRT generated files). I deleted every `bin`/`obj` folder in the worktree and reran everything. The logs are in `D:\Wino-Mail\artifacts\ios-platform\D-*.log`.

| Command | Result |
| --- | --- |
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64` | Succeeded, 0 warnings, 0 errors (`D-winui-debug.log`) |
| `dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` | Succeeded, 0 warnings, 0 errors (`D-winui-release.log`) |
| `dotnet build controls/Wino.Presentation.Core/Wino.Presentation.Core.csproj -c Release -p:Platform=x64` | Succeeded, 0 warnings, 0 errors |
| `dotnet test tests/Wino.Mail.Controls.Tests -c Debug -p:Platform=x64` | 41/41 passed (baseline 34 plus 7 new) |
| `dotnet test tests/Wino.Core.Tests -c Debug -p:Platform=x64` | 1063 passed, 1 failed. The failure is the known `MailRecipientHistoryTests.TheSameMessageInASecondFolder_IsCountedOnce`; the 2 new runtime tests pass. |
| Mac: `dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 … -p:CodesignKey=- -p:CodesignProvision=` | 0 errors, 54 warnings. All warnings are in existing AppKit code, none in changed code (`D-mac-build-arm64.log`). |
| Mac: `dotnet test tests/Wino.Mail.Controls.Tests -c Debug` | 41/41 passed |
| Mac: `dotnet build WinoMail.MacOS.slnx -c Debug -p:WinoTargetPlatform=MacOS …` | 0 errors; it builds `Wino.Presentation.Core` (`D-mac-sln.log`) |

A note on mirroring: the tar pipe from the Windows checkout turns the framework symlinks inside `Sparkle.xcframework` and `WinoStoreKit2.xcframework` into text files, and the build then fails with MT7091 "not a valid framework". In `~/Desktop/wt-D`, I replaced those two xcframeworks with copies from the Mac checkout, which was read only and not modified. This is a mirroring issue, not a problem in the code.

## Remaining risks / pending
- Manual runtime checks are pending:
  - Gmail sign-in on Windows and Mac. DI now builds the receiver through a factory and resolves the presenter with `GetService`.
  - The default signature text on Windows and Mac.
  - Mac Settings pages that use `AnyPropertyBinding` (Intelligence coverage and management).
  - Mac theming and the avatar colours (the palette now comes from parsed hex strings).
- The Mac x64 build and the Release builds were not run. They were not in the required list.
- Merge conflicts are likely. 43 Mac view files only gained one `using` line, but other agents (C, and Mac view owners) may edit the same files. `controls/Wino.Mail.Controls.AppKit` (owned by agent C) has `using`-only changes in 3 files.
- The `Wino.Presentation.Bindings` namespace has the same name as the `Bindings` property on view controllers. This compiles today because member lookup wins. Code that sits inside the `Wino.Presentation.*` namespaces and refers to a bare `Bindings` could become ambiguous.
- Hosts that call `SynchronizeOnceAsync` must have registered everything `InitializeAsync` needs. On iOS the pass also runs the background startup work that initialization queues.
