# F: Apple shared layer (Phase 1, T9 + T10)

Tree: `D:\Wino-Mail`, branch `feature/iOS`, HEAD `80ca7f38`, in the main working tree on top of the staged Phase 0 result. My changes are staged (moves were done with `git mv`; new files were added with `git add`). Nothing is committed or pushed. I did not touch anything under `controls/`.

Result: all requested builds end with 0 errors. The Mac warning set is identical to the Phase 0 baseline. There is no intended behaviour change for the Mac app. Manual runtime checks are still pending.

## T9: `src/Wino.Platform.Apple`

### Moved (`git mv`, then renamed)

| From `Wino.Platform.MacOS` | To `Wino.Platform.Apple` |
| --- | --- |
| `MacOSPaths` | `ApplePaths` (now takes `AppleApplicationIdentity`) |
| `MacOSApplicationIdentity` (static const) | `AppleApplicationIdentity` (instance; value injected) |
| `MacOSConfigurationService` (+ `MacOSPreferencesJsonContext`) | `AppleConfigurationService` (+ `ApplePreferencesJsonContext`) |
| `MacOSFileService`, `MacOSApplicationResourceResolver` | `AppleFileService`, `AppleApplicationResourceResolver` |
| `MacOSAccountCredentialPersistence`, `MacOSDavCredentialStore`, `MacOSGoogleTokenStore` | `Apple…` |
| `Security/MacOSKeychainStore`, `MacOSSecretProtector`, `MacOSKeychainException`/`MacOSKeychainFailure`/`MacOSCredentialMissingException` | `Security/AppleKeychainStore`, `AppleSecretProtector`, `AppleKeychainException`/`AppleKeychainFailure`/`AppleCredentialMissingException` |
| `Services/MacAppMetadataService` | `Services/AppleAppMetadataService` (`platformDisplayName` is now required; the Mac passes `"Mac"`) |
| `Services/MacAppStoreClient` | `Services/AppleAppStoreClient` |
| `Services/MacNotificationCategories` | `Services/AppleNotificationCategories` |
| `Services/MacStatePersistenceService` | `Services/AppleStatePersistenceService` |
| `Services/MacReaderRuntimeService` | `Services/AppleReaderRuntimeService` (guard is now `IsMacOS() \|\| IsIOS()`) |
| `Services/MacNotificationBuilder` (+ `MacDockFolderEntry`) | `Services/AppleNotificationBuilder` (+ `AppleQuickFolderEntry`; `DockFolders` is renamed `QuickFolders`) |
| `Services/MacNotificationSounds` (+ `MacNotificationSoundKind`) | `Services/AppleNotificationSounds` (+ `AppleNotificationSoundKind`) |

Other changes in the moved files:
- Every `[SupportedOSPlatform("macos")]` now also has `[SupportedOSPlatform("ios17.0")]`. A plain `"ios"` raised CA1416 on `AesGcm`, which requires iOS 13 or later.
- I removed one null-forgiving operator: `Path.GetDirectoryName(_path)!` in the configuration service.

### What stays the same for the Mac app

- `AppleKeychainStore(AppleApplicationIdentity)` still produces the service names `com.winomail.macos.v1.<scope>`.
- `ApplePaths` still uses `Application Support/com.winomail.macos` and `$TMPDIR/com.winomail.macos`.
- The MSAL cache stays `com.winomail.macos.v1.msal`: `MacOSOutlookAuthenticationHost` now takes the identity as a constructor argument.
- The sound preference keys stay `MacNotificationSound.*` (commented as a legacy name).
- DI lifetimes are unchanged.
- The only other change is the text of one exception message: "macOS could not deliver…" became "The notification center could not deliver…".

### New project file `src/Wino.Platform.Apple/Wino.Platform.Apple.csproj`

```xml
<PropertyGroup>
  <TargetFrameworks>net10.0-macos;net10.0-ios</TargetFrameworks>
  <TargetFrameworks Condition="$(RuntimeIdentifier.StartsWith('osx-'))">net10.0-macos</TargetFrameworks>
  <TargetFrameworks Condition="$(RuntimeIdentifier.StartsWith('ios-')) or $(RuntimeIdentifier.StartsWith('iossimulator-'))">net10.0-ios</TargetFrameworks>
  <Nullable>enable</Nullable> <ImplicitUsings>enable</ImplicitUsings> <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
<PropertyGroup Condition="$(TargetFramework.Contains('-macos'))"><SupportedOSPlatformVersion>14.0</SupportedOSPlatformVersion></PropertyGroup>
<PropertyGroup Condition="$(TargetFramework.Contains('-ios'))"><SupportedOSPlatformVersion>17.0</SupportedOSPlatformVersion></PropertyGroup>
<ItemGroup>  Wino.Core.Domain, Wino.Authentication, Wino.Services, Wino.Core.Apple.Bindings (ProjectReference) </ItemGroup>
```

- `WinoTargetPlatform` is not set in the project.
- `Directory.Build.targets` imports `Wino.MacOS.targets` or `Wino.iOS.targets` by target framework, so each inner build gets the AOT exemption. I confirmed this: `IsAotCompatible=false` for both target frameworks.

**Why the build narrows its target frameworks by RID** (this departs from the brief):
- `dotnet build Wino.Mail.MacOS.csproj -r osx-arm64` passes `RuntimeIdentifier` to restore for every project in the graph.
- The `net10.0-ios` inner restore then failed with `NU1102 Microsoft.NETCore.App.Runtime.Mono.osx-arm64 (= 10.0.12)`.
- A build that passes an `osx-*` RID therefore contains only `net10.0-macos`, and a build that passes an `ios-*` or `iossimulator-*` RID contains only `net10.0-ios`. The same applies to the bindings project.
- Builds without a RID (the solution build, `-f net10.0-ios`) still build both target frameworks.
- `-r iossimulator-arm64 -p:WinoTargetPlatform=iOS` also builds: 0 errors (`F-mac-platform-apple-iossim-rid.log`).

**Target framework versions:** `net10.0-ios` and `net10.0-macos` are left unpinned. Both libraries resolve `TargetPlatformVersion` 26.0, the same as the existing Mac libraries. If the iOS head pins `net10.0-ios27.0` (G's note), every iOS library has to move with it. Otherwise a 26.0 consumer cannot reference a 27.0 library.

### Hook interfaces (`src/Wino.Platform.Apple/Services/NotificationPlatformHooks.cs`)

```csharp
public interface INotificationImageConverter { bool TryWritePng(string sourcePath, string destinationPath); } // off the UI thread
public interface IApplicationBadgePresenter { void ShowBadge(int count); }                                  // UI thread, 0 clears
public interface INotificationSoundCatalog
{
    IReadOnlyList<string> Sounds { get; }       // sorted selectable names
    string? GetSourcePath(string sound);        // file copied into <home>/Library/Sounds
    void Play(string? sound);                   // preview; null = default alert sound
}
```

**`AppleNotificationBuilder`:**
- It now takes `INotificationImageConverter` and `IApplicationBadgePresenter`.
- It keeps the shared logic: the policy gate, the temp folder `wino-notification-images`, attachments, categories, and the badge snapshot and lock.

**`AppleNotificationSounds`:**
- It now takes `INotificationSoundCatalog`.
- `SystemSounds`, `IsSystemSound` (with `[NotNullWhen(true)]`, which removed two `!`) and `Play` became instance members.
- `Map` and `ContainerSoundsFolder` stay static.
- The container file name is the sound name plus the extension of the source file. On the Mac this is `.aiff`, so nothing changes.

**Mac implementations** are in `src/Wino.Platform.MacOS/Services/MacNotificationPlatformHooks.cs`:
- `MacNotificationImageConverter`: NSImage, then TIFF, then an `NSBitmapImageRep` PNG. This is the former builder code, and it carries the pre-existing CA1422.
- `MacDockBadgePresenter`: `DockTile.BadgeLabel`, using the same text as before.
- `MacNotificationSoundCatalog`: lists `/System/Library/Sounds/*.aiff`, plays them with NSSound, and falls back to NSBeep.

### `Wino.Platform.MacOS` and the Mac head

- `Wino.Platform.MacOS.csproj` references `Wino.Platform.Apple` and keeps `Wino.Core.Apple.Bindings`, because `MacMicrosoftStoreService` calls `StoreKit2Client.RequestReview`.
- `MacOSOutlookAuthenticationHost` now has the parameter `AppleApplicationIdentity identity`.
- `Wino.Mail.MacOS.csproj` adds `ProjectReference` to `Wino.Platform.Apple`, and the bindings reference is renamed.
- `Composition.cs`:
  - It sets `const BundleIdentifier = "com.winomail.macos"` and creates `new AppleApplicationIdentity(...)`, registered as a singleton. The identity is passed to `ApplePaths`, `AppleKeychainStore`, the Outlook host and the metadata service.
  - It registers `INotificationSoundCatalog`, `INotificationImageConverter` and `IApplicationBadgePresenter` with their Mac implementations, all as singletons.
  - The `Apple*` services are registered in the old slots with the same lifetimes.
- Other head files updated:
  - `AppDelegate.Dock.cs`: `QuickFolders`, `AppleQuickFolderEntry`.
  - `AppDelegate.Notifications.cs`: debug bridge commands `notify-sound-play` and `notify-cats` now use the instance APIs and the renamed types.
  - `Views/Settings/NotificationSettingsPageViewController.cs`: `sounds.SystemSounds`, `sounds.Play`.
  - `Infrastructure/MacViewModelRegistration.Notifications.cs`: comment only.
  - `AppDelegate.Updates.cs`, `Views/Settings/AppPreferencesPageViewController.cs`, `Views/Settings/WinoAccountDebug.cs`: `using Wino.Core.Apple.Bindings.*`.
- `WinoMail.MacOS.slnx` gains `src/Wino.Platform.Apple` and the renamed bindings project. Only my two lines changed, so this is H-safe.
- Docs:
  - `docs/harness/development-macos.md`: `ApplePaths`, `AppleKeychainStore`.
  - `docs/harness/implementation-rules.md`: architecture tree.
  - `docs/releases-macos.md` and `scripts/release/macos/build-macos-release.sh`: new bindings path.
  - Not updated: the `AGENTS.md` platform sentence and the dated inventory in `docs/android-port-investigation.md`. Both are left for the documentation step.

## T10: `src/Wino.Core.Apple.Bindings`

**Rename:**
- `git mv src/Wino.Core.MacOS.Bindings src/Wino.Core.Apple.Bindings`, with the project file renamed to match.
- Namespaces are now `Wino.Core.Apple.Bindings`, `.StoreKit2` and `.Sparkle`.
- No `Wino.Core.MacOS.Bindings` references remain outside `docs/android-port-investigation.md`.

**Project file** (key parts):

```xml
<TargetFrameworks>net10.0-macos;net10.0-ios</TargetFrameworks>   (+ the same RID narrowing as above)
<IsBindingProject>true</IsBindingProject>
SupportedOSPlatformVersion 14.0 (-macos) / 17.0 (-ios)
<PropertyGroup Condition="'$(WinoMacDistribution)' == 'AppStore'"> OutputPath bin/$(Configuration)-AppStore/, IntermediateOutputPath obj/... </PropertyGroup>
<WinoIncludeSparkle Condition="$(TargetFramework.Contains('-macos')) and '$(WinoMacDistribution)' != 'AppStore'">true</WinoIncludeSparkle>
ObjcBindingApiDefinition ApiDefinition.cs (both); NativeReference StoreKit2/Native/WinoStoreKit2.xcframework (both, Frameworks=StoreKit)
Sparkle ObjcBindingApiDefinition + NativeReference only when WinoIncludeSparkle; Compile Remove="Sparkle/**" otherwise
```

`IsBindingProject` works unchanged for the iOS inner build; no other setting was needed:
- `bin/Debug/net10.0-ios/Wino.Core.Apple.Bindings.resources.zip` contains only `WinoStoreKit2.xcframework`.
- macOS Debug contains StoreKit2 and Sparkle.
- `Debug-AppStore/net10.0-macos` contains StoreKit2 only.
- A clean App Store bundle has `Contents/Frameworks` = `WinoStoreKit2.framework` only.
- A stale `Sparkle.framework` stays in an `.app` that was built earlier as DMG, because the head has no AppStore output split. This is pre-existing.

**`build-native.sh`** builds three slices, with `xcrun --sdk <sdk> swiftc` so the link step uses the right sysroot:
- **macOS:** `arm64`/`x86_64-apple-macos14.0`, deep framework, same as before.
- **iOS device:** `arm64-apple-ios17.0` (iphoneos).
- **iOS Simulator:** `arm64-apple-ios17.0-simulator` (iphonesimulator).
- **iOS framework layout:** both iOS slices are shallow frameworks (`WinoStoreKit2.framework/WinoStoreKit2` + `Info.plist`) with:
  - `MinimumOSVersion` 17.0
  - `CFBundleSupportedPlatforms` `iPhoneOS` or `iPhoneSimulator`
  - install name `@rpath/WinoStoreKit2.framework/WinoStoreKit2`
- **Signing:** every slice is ad-hoc signed.
- **xcframework:** `xcodebuild -create-xcframework -framework <macos> -framework <ios> -framework <ios-sim>`.
- **Hash:** the hash file now covers the Swift sources and `build-native.sh`.
- The README is updated with the slice table, the canImport rule, iOS embedding and a symlink note.

**`WinoStoreKit2.swift`:**
- The imports are wrapped as `#if canImport(AppKit) import AppKit #elseif canImport(UIKit) import UIKit #endif`.
- `wino_sk2_request_review` keeps the AppKit key-window path.
- Under UIKit it uses `AppStore.requestReview(in:)` on the first `UIWindowScene` whose `activationState == .foregroundActive`. With no such scene it does nothing.

**Native build output** (`./build-native.sh StoreKit2` on burak-mac, Xcode 27; log `F-build-native.log`):
- `plutil -p WinoStoreKit2.xcframework/Info.plist` lists three libraries:
  - `ios-arm64-simulator` (arm64, ios, simulator)
  - `ios-arm64` (arm64, ios)
  - `macos-arm64_x86_64` (arm64 and x86_64, macos)
- `vtool`:
  - device: IOS, minos 17.0, sdk 27.0
  - simulator: IOSSIMULATOR, minos 17.0
  - macOS: MACOS, minos 14.0
- `otool -L`: the iOS slices link UIKit and StoreKit; the macOS slice links AppKit and StoreKit.
- No sysroot warnings.
- `WinoStoreKit2.sources.sha256`:
  - `bf70a174…d37  StoreKit2/Swift/WinoStoreKit2.swift`
  - `deb7014e…db5  build-native.sh`
  - These match the files in the Windows tree.

**Copied back to Windows:**
- Only regular files came back (a tar of `find -type f`; I deleted the macOS `._*` AppleDouble files).
- The three symlink placeholders of the macOS slice are unchanged:
  - `Resources -> Versions/Current/Resources`
  - `Versions/Current -> A`
  - `WinoStoreKit2 -> Versions/Current/WinoStoreKit2`
  - They are still mode 120000 in the index, so nothing differs because of Windows.
- `git status`:
  - Modified: the macOS slice binary, `Info.plist` and `CodeResources`, the xcframework `Info.plist`, and the hash file.
  - New: `ios-arm64/…` and `ios-arm64-simulator/…`, each with the binary, `Info.plist` and `_CodeSignature/CodeResources`. I set both binaries to 100755 to match the macOS binary.

## Verification

Logs are in `artifacts/ios-platform/F-*.log`. Mac commands ran in `~/Desktop/wt-F`, mirrored with the brief's tar command; the two `.xcframework` folders were restored from `~/Desktop/Wino-Mail`, and StoreKit2 was rebuilt there.

| Command | Result |
| --- | --- |
| Windows `dotnet build src/Wino.Mail.WinUI/... -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` | 0 errors, 0 warnings (`F-windows-release-x64.log`) |
| Mac head Debug `-r osx-arm64` | 0 errors. The first full build had 54 warnings, identical to the Phase 0 T8 set outside the platform projects, with the same codes in the moved files. The log kept is a later incremental rerun: 0 errors, 48 warnings (`F-mac-debug-arm64.log`). |
| Mac head Debug `-r osx-x64` | 0 errors, 48 warnings (`F-mac-debug-x64.log`); the bundle's `WinoStoreKit2` is x86_64 |
| Mac head Debug `-r osx-arm64 -p:WinoMacDistribution=AppStore` | 0 errors, 48 warnings on the full build. The log kept is the clean-bundle rerun: 0 errors, 0 warnings (`F-mac-debug-arm64-appstore.log`). |
| `Wino.Platform.Apple.csproj -f net10.0-ios -p:WinoTargetPlatform=iOS` | 0 errors, 7 warnings (`F-mac-platform-apple-ios.log`). All 7 are pre-existing nullable warnings that the Mac build also reports: CS8601 ×5 in `AppleAccountCredentialPersistence`, CS8613 in the DAV and Google token stores. |
| `Wino.Core.Apple.Bindings.csproj -f net10.0-ios -p:WinoTargetPlatform=iOS` | 0 errors, 0 warnings (`F-mac-bindings-ios.log`) |
| `WinoMail.MacOS.slnx` Debug (`WinoTargetPlatform=MacOS`) | 0 errors, 58 warnings (48 unique, all already in the head build) (`F-mac-slnx-debug.log`); both target frameworks of Apple and Bindings were built |

Mirror notes:
- `scp` from Git Bash does not use the Windows agent. Use `ssh.exe … 'cat > file' < file` instead.
- The Windows copy of `build-native.sh` must have LF line endings; the new file has them.

## What the iOS head must register

**From `Wino.Platform.Apple`**, mirroring the Mac `Composition.cs`:
- Create before the container:
  - `new AppleApplicationIdentity("com.winomail.ios")`
  - `new ApplePaths(identity, NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory)`
  - `new AppleKeychainStore(identity)`
  - `new AppleSecretProtector(keychain)`, then `ProvisionNewInstallationKey` when the installation is new
- Register as singletons: `identity`, `paths`, `keychain`, `ISecretProtector`.
- `IConfigurationService` → `AppleConfigurationService`
- `IApplicationResourceResolver` → `AppleApplicationResourceResolver`
- `IFileService` → `AppleFileService`
- `IAccountCredentialPersistence` → `AppleAccountCredentialPersistence`
- `IDavCredentialStore` → `AppleDavCredentialStore`
- `IGoogleTokenStore` → `AppleGoogleTokenStore`
- `IReaderRuntimeService` → `AppleReaderRuntimeService`
- `IStatePersistanceService` → `AppleStatePersistenceService`
- `AppleNotificationSounds`, and `INotificationBuilder` → `AppleNotificationBuilder`. Call `RegisterCategories()` at launch.
- `IAppStoreClient` → `AppleAppStoreClient`
- `IAppMetadataService` → `new AppleAppMetadataService(version, identity.Value, isDebug, build, "iPhone"/"iPad")`
- `AppleNotificationCategories` (static) for the response delegate.

**iOS-only implementations still missing** (for `Wino.Platform.iOS`):
- `INotificationImageConverter` (UIImage PNG).
- `IApplicationBadgePresenter` (`UNUserNotificationCenter.SetBadgeCount`).
- `INotificationSoundCatalog`: an empty list or bundled sounds, with an AudioToolbox preview. With an empty catalog the mapped macOS names fall back to the default sound.
- `IOutlookAuthenticationHost` (MSAL iOS keychain group).
- `IGoogleAuthorizationCodeReceiver`.
- `IExternalLauncher`, `IClipboardService`, `IAttachmentPlatformService`.
- `IMicrosoftStoreService`: rating through `StoreKit2Client.RequestReview`, which now works on iOS, plus the throttle.
- `IShortcutPlatformService`, `IStartupIntegrationService`, `IUserPresenceStateProvider`, `ISmimeCertificateService`, `ITaskCompletionSound`: stubs.
- The `UNUserNotificationCenterDelegate`. `MacNotificationResponseHandler` is still in the Mac head; a shared delegate in Platform.Apple, or an iOS twin, is still to be done.

**Entitlements:** the Keychain items use no access group (the app's default group). MSAL on iOS needs the `keychain-access-groups` entitlement.

## Pending (not performed)

Mac manual checks:
- Launch with existing accounts and confirm Keychain reads work: credentials, the protection key and the MSAL cache under `com.winomail.macos.*`.
- A notification with a sender picture and a sound.
- The Dock badge and the Dock quick folders.
- Settings › Notifications: the sound list and Play.
- App Store build: the purchase flow and the rating prompt.

iOS: none of these builds runs anything; that starts in Phase 3.
