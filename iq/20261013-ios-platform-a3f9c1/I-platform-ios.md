# I: `src/Wino.Platform.iOS` (Phase 3, item 1)

Tree: `D:\Wino-Mail` main working tree, branch `feature/iOS`, on top of the staged Phases 0 and 1. My files are untracked (not staged). Nothing is committed or pushed.
I did not touch `controls/**`, `src/Wino.Mail.MacOS/**`, `src/Wino.Platform.MacOS/**`, `src/Wino.Platform.Apple/**` or any solution file.

## Files

New project `src/Wino.Platform.iOS/` (16 source files):
- `Wino.Platform.iOS.csproj`
- `IOSPlatformRegistration.cs`, which also holds `IOSPlatformBootstrap`
- `Services/`: `IOSClipboardService`, `IIOSFilePresenter`, `IOSExternalLauncher`, `IOSAttachmentPlatformService`, `IOSMicrosoftStoreService`, `IOSOutlookAuthenticationHost`, `IOSGoogleAuthorizationCodeReceiver`, `IOSPresentationAnchorProvider`, `IOSNotificationPlatformHooks` (three hook classes), `IOSTaskCompletionSound`, `IOSShortcutPlatformService`, `IOSStartupIntegrationService`, `IOSUserPresenceStateProvider`, `IOSSmimeCertificateService`, and the internal `IOSMainThread`

Shared additions:
- `src/Wino.Authentication/GoogleCustomSchemeRedirect.cs` (new, pure `net10.0`). It derives the reversed-client-ID scheme and redirect URI, and parses the callback URL (state, error, code). It sits next to `GoogleAuthorizationPkce`, so it can be unit-tested and a later Android head can reuse it.
- `tests/Wino.Core.Tests/Authentication/GoogleCustomSchemeRedirectTests.cs` (new, 12 cases).

## Project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-ios</TargetFramework>
    <SupportedOSPlatformVersion>17.0</SupportedOSPlatformVersion>
    <WinoTargetPlatform>iOS</WinoTargetPlatform>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Wino.Platform.iOS</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Wino.Core.Domain/Wino.Core.Domain.csproj" />
    <ProjectReference Include="../Wino.Authentication/Wino.Authentication.csproj" />
    <ProjectReference Include="../Wino.Services/Wino.Services.csproj" />
    <ProjectReference Include="../Wino.Platform.Apple/Wino.Platform.Apple.csproj" />
    <ProjectReference Include="../Wino.Core.Apple.Bindings/Wino.Core.Apple.Bindings.csproj" />
    <PackageReference Include="Microsoft.Identity.Client" />
  </ItemGroup>
</Project>
```

- `WinoTargetPlatform` is set in the project, as `Wino.Platform.MacOS` does.
- `build/Wino.iOS.targets` supplies `IsAotCompatible=false` and `PublishAot=false`.
- The code uses no reflection emit and no suppressions. It has no null-forgiving operators. I added no `[SupportedOSPlatform]` attributes: the whole assembly is iOS 17+, so the platform analyzer is already satisfied, and the one newer API (the iOS 17.4 session callback) has a version guard.
- Namespaces: `Wino.Platform.iOS` (registration) and `Wino.Platform.iOS.Services`, mirroring the Mac project.

## Services and outcome semantics

| Service | Behaviour |
| --- | --- |
| `IOSClipboardService` | Sets `UIPasteboard.General.String` on the UI thread (`IDispatcher`). Succeeded only if `HasStrings` is true afterwards (or the text is empty), otherwise Failed. |
| `IOSExternalLauncher` | **URIs:** only http, https and mailto are accepted, the same allowlist as the Mac; anything else is Failed. The URI opens through `UIApplication.OpenUrlAsync` on the UI thread: Succeeded or Failed per the completion result.<br>**Files:** a missing file is Failed. With no presenter registered, the result is Unavailable. Otherwise the call goes to the last registered `IIOSFilePresenter`. |
| `IIOSFilePresenter` (defined here, implemented by the head with QuickLook or `UIDocumentInteractionController`) | `Task<PlatformOperationResult> PresentFileAsync(string fullPath, CancellationToken)`. The documented results are Succeeded, Cancelled, Unavailable and Failed. |
| `IOSAttachmentPlatformService` | **`ApplySavePolicyAsync`:** there is no quarantine step. The result is Cancelled if the token is cancelled, Failed if the file is missing, otherwise Succeeded.<br>**`OpenReceivedAsync`:** goes through the launcher and maps its status, the same as the Mac service. |
| `IOSMicrosoftStoreService` | **Rating throttle:** copied from `MacMicrosoftStoreService`, because the Mac class is AppKit-bound with no reusable core. It waits 3 days after the first trigger, then allows one request every 120 days, and skips when a debugger is attached. The keys are `IOSRating*`.<br>**Review prompt:** `StoreKit2Client.RequestReview()` runs on the UI thread.<br>**Write-review page:** `itms-apps://apps.apple.com/app/id6820215639?action=write-review`. The App ID is the Mac App ID used as a placeholder; a comment explains that the iOS App Store record will have its own ID.<br>**Purchases and updates:** the same no-op answers as the Mac (`PurchaseAsync` throws `PlatformNotSupportedException`). |
| `IOSOutlookAuthenticationHost` | **Client:** MSAL client ID from `IAuthenticatorConfig`, authority `common` (as on the Mac), redirect `msauth.<identity.Value>://auth` built from `AppleApplicationIdentity`, `WithIosKeychainSecurityGroup("com.microsoft.adalcache")`. MSAL logging is forwarded as on the Mac.<br>**Cache:** there is no `MsalCacheHelper`. `EnsureTokenCacheAttachedAsync` is a no-op because MSAL keeps its own Keychain cache on iOS.<br>**`CanAuthenticateInteractively`:** true only while the head's `Func<UIViewController?>` returns a controller whose view is in a window. This is read on the main thread.<br>**Interactive sign-in:** uses `WithUseEmbeddedWebView(false)` (system web view, i.e. ASWebAuthenticationSession) and `WithParentActivityOrWindow(controller)`. It throws `InvalidOperationException` when there is no presenter. |
| `IOSGoogleAuthorizationCodeReceiver` | **Redirect:** `GoogleCustomSchemeRedirect.GetRedirectUri(GmailAuthenticatorClientId)`, giving `com.googleusercontent.apps.<id>:/oauthredirect`. PKCE and state come from `GoogleAuthorizationPkce`.<br>**Session:** `ASWebAuthenticationSession` on the UI thread. On iOS 17.4+ it uses the `ASWebAuthenticationSessionCallback` initializer; older versions use the scheme initializer. The anchor comes from `IOSPresentationAnchorProvider`.<br>**Cancellation:** the user cancelling (`CanceledLogin`) or the caller's token gives `OperationCanceledException`, and the token also cancels the session.<br>**Errors:** a state mismatch, an `error=` value, a missing code or a failed start each raise `InvalidOperationException`. The messages match the loopback receiver. |
| `IOSPresentationAnchorProvider` | **Anchor order:** the window of the head's controller, then the key window of the foreground-active scene, then any scene window.<br>**`HasAnchor`:** checked before the session starts, so the last-resort `new UIWindow(scene)` is not reached in practice. |
| `IOSNotificationImageConverter` | `UIImage.FromFile(src)?.AsPNG()?.Save(NSUrl.FromFilename(dst), true)`. Returns false on any failure. |
| `IOSApplicationBadgePresenter` | `UNUserNotificationCenter.Current.SetBadgeCount(max(0, n))`. Errors are logged. |
| `IOSNotificationSoundCatalog` | `Sounds` is empty and `GetSourcePath` returns null, because iOS cannot list or copy system sounds; every stored sound name therefore falls back to the default sound. `Play` previews system sound 1007 (the default "Tri-tone"). |
| `IOSTaskCompletionSound` | System sound 1057 ("Tink"). |
| `IOSShortcutPlatformService` | `PrimaryCommandModifier = Command`. `IsShiftKeyPressed()` is false because UIKit has no global modifier state. |
| `IOSStartupIntegrationService` | Both calls return `StartupBehaviorResult.Unavailable`. |
| `IOSUserPresenceStateProvider` | `IsPresenting()` and `IsSystemQuietTimeActive()` are both false. **Deviation from the brief:** I did not use `ApplicationState`. It reports Wino's own state, which does not map to "another app is presenting"; while Wino is in the foreground, the notification delegate decides how a notification is shown. |
| `IOSSmimeCertificateService` | `CreateContext` and `ImportCertificate` throw `PlatformNotSupportedException`, as the interface documents for unavailable implementations. `GetCertificates` returns an empty list. `RemoveCertificate` is a no-op, since nothing is ever stored. |
| Reader runtime | Reused. `AppleReaderRuntimeService` already guards `IsMacOS() \|\| IsIOS()` (from F), so there is no iOS twin. |

## Registration

```csharp
public sealed record IOSPlatformBootstrap(AppleApplicationIdentity Identity, ApplePaths Paths, AppleKeychainStore Keychain, AppleSecretProtector Protector)
{ public static IOSPlatformBootstrap Create(AppleApplicationIdentity identity); }

public static IServiceCollection AddIOSPlatformServices(this IServiceCollection services, AppleApplicationIdentity identity, Func<UIViewController?> presenter);
public static IServiceCollection AddIOSPlatformServices(this IServiceCollection services, IOSPlatformBootstrap bootstrap, Func<UIViewController?> presenter);
```

**Bootstrap (`IOSPlatformBootstrap.Create`):** builds the paths, keychain and protector before the container, as the Mac `Composition` does: `ApplePaths` (Application Support and temp inside the app container, via `NSSearchPathDirectory.ApplicationSupportDirectory`), the keychain, the protector, provisioning on a new installation, and `CreateDirectories`. The second overload lets the head configure logging first.

**Calling order:** the head calls `AddIOSPlatformServices` on the main thread (it reads the idiom), after `RegisterSharedServices` and `RegisterCoreServices`. It uses `Replace` for `IApplicationConfiguration` and `IGoogleAuthorizationCodeReceiver`.

**What it registers:**
- **Pre-built objects:** the identity, `ApplePaths`, `AppleKeychainStore` and `ISecretProtector`, plus the `ApplicationConfiguration` replacement.
- **Apple services, mirroring the Mac list:**
  - `IConfigurationService`, `IPreferencesService`, `IApplicationResourceResolver` and `IFileService`.
  - The credential and token stores, `IAuthenticatorConfig`, `IReaderRuntimeService` and `IStatePersistanceService`.
  - `AppleNotificationSounds`, `INotificationBuilder` and `IAppStoreClient`.
  - `IAppMetadataService` (`PlatformDisplayName` is "iPadOS" or "iOS" by idiom).
- **iOS implementations:** all services in the table above, plus `IOSPresentationAnchorProvider` and `PlatformCapabilities(Printing: true, PdfExport: true, Smime: false, AdditionalWindows: isPad, Notifications: true, NotificationActions: true, StartupIntegration: false, Tray: false, GlobalHotkeys: false, GeneralActivation: true, AppleAppStore: true)`.

**Still registered by the head:**
- `IDispatcher`, the theme, dialog and navigation services, and the view models.
- An optional `IIOSFilePresenter`.
- The `UNUserNotificationCenterDelegate`: the Mac handler is still in the Mac head, per F.
- A call to `AppleNotificationBuilder.RegisterCategories()` at launch.

## Head Info.plist and entitlements

- **`CFBundleURLTypes` schemes:**
  - `msauth.com.winomail.ios`
  - `com.googleusercontent.apps.<ios-client-id-prefix>`, the reversed iOS Google client ID
  - plus `mailto`, `wino`, `webcal` and `webcals` per the plan
- **`LSApplicationQueriesSchemes`:** `msauthv2`, `msauthv3`.
- **`keychain-access-groups`:** `$(AppIdentifierPrefix)com.winomail.ios` (the default group used by `AppleKeychainStore`) and `$(AppIdentifierPrefix)com.microsoft.adalcache` (MSAL).
- **Badge authorization:** the notification authorization request must include badge.

## External registrations (user)

- **Azure app registration** (client `b19c2035-…`): add the iOS/macOS platform redirect `msauth.com.winomail.ios://auth`, with bundle ID `com.winomail.ios`.
- **Google Cloud:** create an iOS OAuth client for bundle `com.winomail.ios`.
  - **Open gap:** `MailAuthenticatorConfiguration.GmailAuthenticatorClientId` returns the desktop client ID. Google does not accept a custom-scheme redirect for that client type.
  - **What the head must do:** register an `IAuthenticatorConfig` whose `GmailAuthenticatorClientId` is the new iOS client ID. Once that ID exists, this could be a decorator around `MailAuthenticatorConfiguration`, or a new iOS property.
  - **Scheme:** the receiver derives its scheme from whichever ID the config returns.
- **App Store record:** when it exists, replace `IOSMicrosoftStoreService.AppStoreAppId` with the iOS Apple ID.

## Platform.Apple hooks

No interface change was needed; F's three hooks were enough.

## Verification

Logs are in `D:\Wino-Mail\artifacts\ios-platform\I-*.log`.

**Mac setup:**
- The Mac mirror is `~/Desktop/wt-I`, made with the brief's tar command.
- `WinoStoreKit2.xcframework` came from `~/Desktop/wt-F` (three slices) and `Sparkle.xcframework` from `~/Desktop/Wino-Mail`.
- The mirror's sources are byte-identical to the Windows tree (md5 compared after the last edit).

| Command | Result |
| --- | --- |
| `dotnet build src/Wino.Platform.iOS/... -c Debug -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true` (Mac) | **0 errors, 0 warnings.** The first build had 2 CA1422 warnings in my code: `NSData.Save(string)` and `new UIWindow()`. Both are fixed, and the incremental rebuild has 0 warnings. The 7 known Platform.Apple warnings only appear when that project compiles; I kept the incremental log (`I-ios-debug.log`). |
| Same, `-c Release` (Mac) | **0 errors, 7 warnings.** All 7 are the pre-existing Platform.Apple nullable warnings F reported (CS8601 ×5, CS8613 ×2); none are in `Wino.Platform.iOS` (`I-ios-release.log`). |
| `dotnet build src/Wino.Mail.MacOS/... -c Debug -r osx-arm64 …` (Mac) | **0 errors, 54 warnings**, the same count as the Phase 0/F baseline (`I-mac-debug-arm64.log`). |
| Windows Release gate (`Wino.Mail.WinUI`, Release x64, `GenerateAppxPackageOnBuild=false`), run because `Wino.Authentication` changed | **0 errors, 0 warnings** (`I-windows-release-x64.log`). |
| `dotnet test tests/Wino.Core.Tests --filter Wino.Core.Tests.Authentication` (Windows) | **36/36 passed**, including the 12 new cases (`I-core-tests-authentication.log`). |

## Pending / not done

- **Runtime:** nothing ran on a simulator or device. There is no iOS head yet (Phase 3 items 2–5).
- **Manual checks once the head exists:**
  - Outlook sign-in (MSAL system web view, Keychain cache persistence across launches).
  - Google sign-in (needs the iOS client ID first), including cancelling the sheet.
  - Opening a URL, and opening a file through the file presenter.
  - Clipboard.
  - The rating prompt (it does not show under TestFlight or with a debugger attached).
  - The badge.
  - Sound previews (1007, 1057).
  - The PNG notification attachment.
- **Solution:** `WinoMail.iOS.slnx` does not exist yet, and I did not add the project to `WinoMail.MacOS.slnx` (H shares that file). The documentation step should add it to the planned iOS solution.
- **Untested choice:** system sound IDs 1007 and 1057 are well-known IDs, but they are not formally documented API.
