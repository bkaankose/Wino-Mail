# T3 Apple (shared layer, iOS head, macOS head): report

## Checkpoints

- 13 Oct 2026: read plan, T1 report, Apple harness docs. Starting implementation.
- Code written: Apple shared, iOS head, macOS head, settings pages, plist and doc changes.
- Mac mirror `~/Desktop/wt-relay` created; Mac Debug arm64/x64 and iOS Simulator Debug builds pass.
- Simulator launch found a launch-time relay pass before the database was open (fixed in the iOS head, see Findings); rebuilt and relaunched clean.
- 14 Oct 2026: simulator and mirror deleted. Done. Nothing committed.

## Files

Added:
- `src/Wino.Platform.Apple/PushRelay/AppleKeychainPushRelayDeviceIdentityStore.cs` (scope `push-relay`, account `device-identity-v1`, `PushRelayJsonContext.Default.PushRelayDeviceIdentity`, `SecAccessible.AfterFirstUnlockThisDeviceOnly`; null on missing, empty or corrupt JSON; Keychain errors such as a locked keychain propagate so the service backs off instead of re-registering).
- `src/Wino.Platform.Apple/PushRelay/ApplePushTokenProvider.cs` (`SetToken(NSData)` -> lowercase hex, `TokenChanged` only on change, `SetFailure`).
- `src/Wino.Platform.Apple/PushRelay/ApplePushPayload.cs` (`ToPayload(NSDictionary)`: top-level string values, `aps` skipped).
- `src/Wino.Platform.iOS/Services/IOSPushRelayDeviceInfo.cs` (`ios`, `UIDevice.Name`, `"iOS " + SystemVersion`; values captured on the main thread at registration).
- `src/Wino.Platform.MacOS/Services/MacPushRelayDeviceInfo.cs` (`macos`, computer name, `"macOS x.y[.z]"`).
- `src/Wino.Mail.MacOS/AppDelegate.PushRelay.cs`.

Changed:
- `src/Wino.Platform.Apple/Security/AppleKeychainStore.cs`: `Write`/`TryAdd` overloads with `SecAccessible`; existing overloads unchanged in behaviour.
- `src/Wino.Platform.iOS/IOSPlatformRegistration.cs`: token provider (self + `IPushTokenProvider`), identity store, device info; capabilities `RemoteNotifications: true, PushRelayOptIn: false`.
- `src/Wino.Mail.iOS/AppDelegate.cs`: token/failure forwarding; `DidReceiveRemoteNotification` reads the payload, awaits `_startup`, runs the handler with a 25 s budget (plus a `Task.WhenAny` guard if the handler ignores cancellation), maps `NewData`/`Failed`/other -> `NoData`, completion handler guarded by `Interlocked` so it runs exactly once. "Separate workstream" comment replaced.
- `src/Wino.Mail.iOS/LifecycleHooks.cs`: background refresh calls `CheckInAsync` after `SynchronizeOnceAsync` (unless expired; errors logged); new `RequestForegroundRelaySync()`.
- `src/Wino.Mail.iOS/SceneDelegate.cs`: `sceneWillEnterForeground:` -> `RequestSync(Foreground)` through the hook.
- `src/Wino.Mail.iOS/Entitlements.plist`, `docs/harness/development-ios.md`: relay registration described instead of "separate workstream".
- `src/Wino.Mail.UIKit/Views/Settings/NotificationSettingsPageViewController.cs`: "Live notifications" expander after Tasks without a switch: sign-in InfoBar, status card (hidden while sign-in is required, to avoid the same sentence twice), mailbox warning card, test-push card (`SendPushTestCommand`, visible by `IsPushTestVisible`, result in the description).
- `src/Wino.Mail.MacOS/Composition.cs`: same Apple registrations plus `MacPushRelayDeviceInfo`; capabilities `true/true`.
- `src/Wino.Mail.MacOS/AppDelegate.cs`: `partial void PushRelayServicesReady()` declared and called after `AppStoreServicesReady()`.
- `src/Wino.Mail.MacOS/Views/Settings/NotificationSettingsPageViewController.cs`: same expander with the header switch (`IsPushRelayEnabled`, enabled by `IsPushRelayToggleEnabled`, visible by `IsPushRelayToggleVisible`).
- `src/Wino.Mail.MacOS/Entitlements.AppStore.plist`: `com.apple.developer.aps-environment` = `production`.
- `src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj`: `CustomEntitlements` `com.apple.developer.aps-environment` = `development` when a provisioning profile signs a non-App Store build.
- `docs/harness/development-macos.md`: APNs entitlement and profile note.

## Deviations

- Mac entitlement key is `com.apple.developer.aps-environment` (the macOS key), not `aps-environment`.
- `Entitlements.plist` is unchanged. It also signs the Developer ID DMG (`build-macos-release.sh` passes `-p:CodesignProvision=`), and it signs ad hoc builds. macOS refuses to launch a bundle that has this restricted entitlement but no provisioning profile. The csproj adds the development value only when `CodesignProvision` is set, which keeps the DMG and ad hoc builds launchable. As a result, DMG users register with the relay without a push token and get no pushes. A Developer ID provisioning profile with Push would fix that (product decision).
- Both installed Mac profiles already include push: **Wino Mail macOS Development** = development and **Wino Mail macOS App Store** = production (checked on the Mac).
- `MacPushRelayDeviceInfo` uses `SCDynamicStoreCopyComputerName` (P/Invoke, fallback `Environment.MachineName`). `NSHost.Current.LocalizedName` raised the CA1422 obsolete warning.
- The iOS foreground request goes through `BackgroundRefreshLifecycleHook`, so it is ignored until services are ready (see Findings).
- On macOS, `SecAccessible` applies only to the data-protection keychain; the sandboxed Mac app uses the file keychain, where the class is expected to be ignored. Not verified at runtime.

## Findings for the shared-core owner

- `PushRelayRegistrationService.RequestSync` before `StartAsync` runs a pass at once. On iOS, `sceneWillEnterForeground:` fires during launch before the database opens. That pass threw a `NullReferenceException` in `WinoAccountSessionService.ReadMetadataAsync` (`databaseService.Connection` was null), which the service caught and logged as "Relay registration pass failed unexpectedly". The heads now gate their calls. A shared guard (ignore or defer requests until `StartAsync`) would be more robust.

## Verification (Mac `burak-mac`, mirror `~/Desktop/wt-relay`)

- Mac Debug arm64, ad hoc (`-p:CodesignKey=- -p:CodesignProvision=`): succeeded, 0 errors.
- Mac Debug x64, ad hoc: succeeded, 0 errors. No warnings in changed files after the NSHost fix. Remaining CA1422 warnings are in existing code.
- With `CodesignProvision="Wino Mail macOS Development"`, the generated `Entitlements.xcent` contains `com.apple.developer.aps-environment = development`. Ad hoc bundle: no APNs key, as intended.
- A fully signed Debug build through the `winosign` screen session failed in the existing `WinoSignSparkleHelpers` step with `errSecInternalComponent` (login keychain not reachable). This is an environment issue; the bundle's entitlements already showed the development APNs key.
- iOS Simulator Debug (`-r iossimulator-arm64 -p:WinoTargetPlatform=iOS`): succeeded, 0 errors, no warnings in changed files.
- Simulator launch (dedicated iPhone 18 Pro, iOS 27.0): the app starts and shows Welcome. The log has `Remote notification registration failed: no valid "aps-environment" entitlement`. No crash. After the fix, no errors in the app log.
- Cleanup: simulator deleted, `~/Desktop/wt-relay` deleted (confirmed absent). `~/Desktop/Wino-Mail` and `~/Desktop/wino-ios` were not touched apart from reading the xcframeworks.

## Strings

None new. The keys added by T1 cover the UI.

## Pending

- Mac Release arm64/x64 builds and `codesign -d --entitlements` on signed bundles; App Store build entitlement check (needs a working signing session).
- Signed iOS device build: token arrives, device registers, `DidReceiveRemoteNotification` -> inbox sync, test push (plan section 6, checks 3-9).
- Mac: opt-in switch, device listed with name and macOS version, test push arrives.
- UI checks of both settings pages (light/dark, iPhone/iPad, Mac).
- Relay APNs sandbox flag must match `aps-environment`.
