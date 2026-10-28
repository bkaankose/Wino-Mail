# iOS development

This is the guide for the universal UIKit application in `src/Wino.Mail.iOS` and its solution `WinoMail.iOS.slnx`.
One app bundle serves iPhone and iPad. `Composition.cs` registers the UI project `Wino.Mail.UIKit.Phone` on both idioms (owner, 27 Oct 2026); on iPad it keeps the iPhone design and pairs the list with the open item in wide landscape windows. It builds on the idiom-neutral `Wino.Mail.UIKit`.
The app uses the shared .NET services, ViewModels, translations, and editor engine through explicit UIKit adapters. It does not use Uno, MAUI, Mac Catalyst, or XAML.

Shared rules for every platform are in [AGENTS.md](../../AGENTS.md) and the [implementation rules](implementation-rules.md).
Presentation decisions are in [iOS design decisions](../ios-design-decisions.md). The Apple shared layer (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `Wino.Editor.WebKit`) is also used by the Mac app; see [macOS development](development-macos.md).

## Requirements

- A Mac with Apple Silicon. iOS builds, the Simulator, and signing run on the Mac only.
- The .NET SDK selected by `global.json`, with the `ios` workload: `dotnet workload install ios`. Mac development needs it too, because the multi-targeted Apple libraries restore their iOS target when no RID is passed.
- The Xcode version that the installed workload requires, selected with `xcode-select`, with the iOS SDK.
- An iOS Simulator runtime. A fresh Xcode has none: `xcodebuild -downloadPlatform iOS` (several GB). Check with `xcrun simctl list runtimes`.

Last verified combination (2026-10-13): .NET SDK 10.0.401, iOS workload 27.0.10722, Xcode 27.0 (27A266a), iOS 27.0 Simulator runtime (iPhone 18 Pro, iPad Pro 11-inch and 13-inch (M5)).

## Platform selection

iOS builds pass `-p:WinoTargetPlatform=iOS` explicitly. The host never selects it: a Mac host resolves `MacOS` by default. The head, the `Wino.Mail.UIKit*` projects, and `Wino.Platform.iOS` also set it in their project files.
`Directory.Build.targets` imports [build/Wino.iOS.targets](../../build/Wino.iOS.targets) when `WinoTargetPlatform` is `iOS` or the target framework contains `-ios`. That file sets `IsAotCompatible=false` and `PublishAot=false` for `-ios` targets and defaults `SupportedOSPlatformVersion` to 17.0. Like the AppKit projects, `-ios` projects are exempt from the shared Release gate; the UIKit libraries set `TreatWarningsAsErrors` for Release themselves. Portable libraries keep their Native AOT and trimming gates.

Runtime identifiers come from the iOS SDK, not from Wino policy:

- Libraries stay portable (no RID).
- `dotnet build` of the app uses `iossimulator-arm64` on Apple Silicon.
- The head sets `ios-arm64` for Release builds when no RID is passed; `dotnet publish` uses `ios-arm64`.

Multi-targeted Apple libraries (`net10.0-macos;net10.0-ios`) narrow to `net10.0-ios` when an `ios-*` or `iossimulator-*` RID is passed, and to `net10.0-macos` for an `osx-*` RID. Use the unversioned `net10.0-ios` target framework in every iOS project: libraries resolve platform version 26.0 and the app 27.0, and a library pinned to a newer version than its consumer cannot be referenced.

The app runs on the Mono runtime with the SDK defaults (no interpreter setting was needed for the Simulator). Native AOT stays off.

## Build

```sh
dotnet build src/Wino.Mail.iOS/Wino.Mail.iOS.csproj -c Debug -r iossimulator-arm64 -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true
dotnet build WinoMail.iOS.slnx -c Debug -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true
```

Output is `src/Wino.Mail.iOS/bin/Debug/net10.0-ios/iossimulator-arm64/Wino.Mail.iOS.app`; the display name is "Wino Mail". A single library builds the same way, for example `dotnet build src/Wino.Platform.Apple/Wino.Platform.Apple.csproj -f net10.0-ios -p:WinoTargetPlatform=iOS`.

`WinoMail.iOS.slnx` has four folders: `/iOS/` (head, UIKit projects, `Wino.Platform.iOS`, UIKit controls), `/Apple/` (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `Wino.Editor.WebKit`), and `/Shared/`.

Device builds need the signing identity and profile below, which do not exist yet:

```sh
dotnet build src/Wino.Mail.iOS/Wino.Mail.iOS.csproj -c Release -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true
dotnet publish src/Wino.Mail.iOS/Wino.Mail.iOS.csproj -c Release -r ios-arm64 -p:WinoTargetPlatform=iOS -p:EnableWindowsTargeting=true
```

No iOS Release or device build of the head has been run. Pass `-p:CodesignKey=... -p:CodesignProvision=...` to sign with another identity.

The head bundles the WinoIcons fonts (`Fonts/`), `WinoLogo.png`, the theme wallpapers (`Themes/`), the What's New notes (`Assets/WhatsNew/`), and the ML models (`Models/`) at the `.app` root, which is `AppContext.BaseDirectory`. It references `Microsoft.ML.OnnxRuntime` directly, because the package's iOS framework comes from build targets that do not flow through project references.

## Identity, signing, and entitlements

| Item | Value |
| --- | --- |
| Bundle identifier | `com.winomail.macos` (`ApplicationId` in the project, `CFBundleIdentifier` in `Info.plist`), the same as the Mac app. Both platforms ship in one App Store record (Apple ID 6820215639, universal purchase). |
| Team | `4VB7YWRAQ9` |
| Minimum OS | iOS and iPadOS 17.0 (`SupportedOSPlatformVersion`, `MinimumOSVersion`) |
| Device families | iPhone and iPad (`UIDeviceFamily` 1, 2) |
| Version | Read from `src/Wino.Mail.WinUI/Package.appxmanifest`, as on the Mac |
| Simulator signing | Ad hoc by the SDK, without `Entitlements.plist` |
| Device signing | `Apple Development` with the **Wino Mail iOS Development** profile, and `Entitlements.plist` (`aps-environment` development). Applied only for `ios-*` RIDs. |
| App Store signing | Pass `-p:WinoIOSDistribution=AppStore` with `-c Release -r ios-arm64`: `Apple Distribution` with the **Wino Mail iOS App Store** profile and `Entitlements.AppStore.plist` (`aps-environment` production). Signed builds over SSH run through the user-level `winosign` screen session (`~/bin/wino-gui-run.sh`). |
| Distribution | App Store only. `WINO_APPSTORE` is always defined, so StoreKit purchases and the App Store client are always on. |

`Entitlements.plist` holds:

- `keychain-access-groups`: `$(AppIdentifierPrefix)com.winomail.macos` (the default group `AppleKeychainStore` uses) and `$(AppIdentifierPrefix)com.microsoft.adalcache` (the MSAL token cache).
- `aps-environment`: `development`. The APNs token goes to `ApplePushTokenProvider` (`Wino.Platform.Apple`), and the shared relay service registers the device with WinoRelay for the signed-in Wino Account. Data-only pushes arrive in `AppDelegate.DidReceiveRemoteNotification` and run an inbox sync for the account in the payload. The device id is a Keychain item (scope `push-relay`, this device only). The relay's APNs sandbox flag must match the build's `aps-environment`.

`EnableDefaultCodesignEntitlements` is false: with the entitlements applied, the SDK looked for a provisioning profile even for the Simulator. Simulator consequences: the log reports no `aps-environment` entitlement, BGTaskScheduler is not available, and the MSAL Keychain group is missing, so Outlook sign-in can fail with -34018 until it runs signed on a device.

## Info.plist, storage, Keychain, and activation

- URL schemes (`CFBundleURLTypes`): `mailto`, `wino`, `webcal`/`webcals`, `msauth.com.winomail.macos` (MSAL redirect `msauth.com.winomail.macos://auth`), and the reversed Google iOS client ID `com.googleusercontent.apps.REPLACE-WITH-IOS-CLIENT-ID` (a placeholder until the client exists). `SceneDelegate` skips the sign-in schemes, which the web-authentication session receives.
- `LSApplicationQueriesSchemes`: `msauthv2`, `msauthv3`.
- Document types: `.ics`, vCard, and `.eml` as an alternate handler.
- `UIFileSharingEnabled`: files Wino saves land in Documents, visible in the Files app. iOS has no save panel that returns a path.
- `UIBackgroundModes`: `fetch`, `processing`, `remote-notification`. `BGTaskSchedulerPermittedIdentifiers`: `com.winomail.macos.refresh` and `com.winomail.macos.processing`; both run `IApplicationRuntime.SynchronizeOnceAsync`.
- Scenes: `UIApplicationSupportsMultipleScenes` is true, but the app opens no second scene; `SceneDelegate` closes any extra main-window scene.
- `ITSAppUsesNonExemptEncryption` is false.
- Application data: `Library/Application Support/com.winomail.macos` in the app container (database, preferences, logs in `Logs/`, MIME files). `ApplePaths` (Wino.Platform.Apple) resolves it.
- Temporary files: `tmp/com.winomail.macos` in the app container.
- Credentials: generic Keychain passwords with the service name `com.winomail.macos.v1.<scope>` (`AppleKeychainStore`; the identity comes from `Composition.cs`). MSAL keeps its own Keychain cache.

Preserve application data, Keychain items, and the bundle identifier.

## Run in the Simulator

```sh
xcrun simctl list devices available
xcrun simctl boot "iPhone 18 Pro"
xcrun simctl install booted src/Wino.Mail.iOS/bin/Debug/net10.0-ios/iossimulator-arm64/Wino.Mail.iOS.app
xcrun simctl launch --console-pty booted com.winomail.macos
xcrun simctl io booted screenshot iphone.png
```

When several agents share one Mac, create a simulator per task (`xcrun simctl create "<name>" "iPhone 18 Pro"`) and address it by UDID, so installs do not replace each other's builds.
The headless Simulator ignores rotation requests; check rotation and landscape in the Simulator app.

### Debug bridge

Debug builds start `UIKitDebugBridge` (`src/Wino.Mail.UIKit/Infrastructure`), the twin of the Mac `MacDebugBridge`. It polls `tmp/wino-debug/cmd` in the app container, runs one command per line, deletes the file, appends results to `out.log` followed by `DONE`, and writes snapshots to `NAME.png` in the same folder:

```sh
dir="$(xcrun simctl get_app_container booted com.winomail.macos data)/tmp/wino-debug"
printf 'seedmail\nsnap inbox\n' > "$dir/cmd"
cat "$dir/out.log"
```

| Area | Commands |
| --- | --- |
| Built in | `snap NAME`, `mode Mail\|Calendar\|Contacts\|Tasks`, `page WinoPage`, `settings`, `back`, `theme NAME`, `appearance light\|dark\|system`, `iconstyle mono\|color`, `sleep MS` |
| Seed data | `seedmail` (local demo IMAP account with folders and mails), `seedpeople` (local address book), `seedtasks` (two task lists), `seedcalendar [ADDRESS]` (turns calendar on for the first account, adds "Demo calendar" and a read-only "Holidays" calendar with 11 events this week, then opens Calendar; run it before Calendar first loads its calendars, or relaunch afterwards) |
| Mail pages | `open-reader`, `open-compose`, `open-idle`, `idle-dump`, `briefing [fake]`, `whatsnew`, `eml-sample`, `eml-open PATH`, `eml-activate [PATH]`, `close`, `reader-*` and `compose-*` |
| Settings | `settingspage WinoPage`, `dialog NAME`, `dismiss`, `iconsize` |
| Calendar | `calinfo`, `caltype Day\|Week\|WorkWeek\|Month`, `calprev`, `calnext`, `caltoday`, `calselect N`, `caldetails-close`, `calnew`, `calnew-notes`, `calcompose-close`, `calslot` (quick event at a slot), `calquick NAME`, `calmenu N`, `cal-*` (details, composer, search); iPhone `calagenda on\|off`, `calheader`, `calcalendars` |
| Shell (iPhone and iPad) | `mailboxes`, `openfolder`, `drawer open\|close`, `toolbarinfo`, `stackinfo`, `pop`, `readertoolbar`, `readertap N`, `newmail`, `moderoot`, `moderoot-tap N` (To Do Lists), `selectmail N` |
| iPad | `pairing` (list and item columns of every tab), `pairing close` (closes the item beside the list). Simulator taps may not land in landscape; drive landscape through the bridge. |
| Contacts and To Do | `contacts-*`, `todo-*` |

The seed commands create a local IMAP account with a closed loopback port and placeholder credentials, so synchronization fails by design. A walk on a fresh simulator runs `seedmail`, `seedpeople`, `seedtasks` and `seedcalendar`, then relaunches the app. The bridge runs a file's lines back to back (`sleep` does not delay the next line), so write one command file per step when a step waits for an animation, and note that `settingspage` opens Settings itself (after `settings` it stacks a second Settings root). The Debug bridge and the seed data compile only in Debug. Feature code adds commands with `UIKitDebugBridge.Register` inside `#if DEBUG`.

## UIKit pitfalls

- Never construct a `UIButton` subclass with `base(UIButtonType.System)`: `buttonWithType:` returns a plain native `UIButton`, so the managed subclass instance is not the native object's peer, the GC collects it, and its event handlers, fields and identity are gone (the folder customization rows stopped reacting to taps and drags, 18 Oct 2026). Use the parameterless constructor (alloc/init) with a `UIButtonConfiguration`; plain `new UIButton(UIButtonType.System)` without a subclass is fine.
- `ReportError` in `WinoViewController` writes to the local log and to telemetry; telemetry alone drops the exception when the user has not opted in, so a page error is only visible in `Logs/wino<date>.log`.

## Building from a Windows checkout

The Windows host has no iOS toolchain; iOS builds run on the Mac. When the Mac builds a mirror of a Windows working tree:

- The xcframework folders under `src/Wino.Core.Apple.Bindings` contain symbolic links. A Windows checkout without `core.symlinks` stores them as text files, and the build fails with MT7091 "not a valid framework". Restore the links listed by `git ls-files -s | grep ^120000`, or copy the `.xcframework` folders from a checkout made on the Mac.
- Shell scripts such as `build-native.sh` need LF line endings.
- Leave `.claude/` (agent worktrees) and `src/Wino.Mail.WinUI/AppPackages` out of the mirror.

## VS Code

Open [WinoMail.iOS.code-workspace](../../WinoMail.iOS.code-workspace) on the Mac. It selects `WinoMail.iOS.slnx`. **Ctrl+Shift+B** runs **Build Wino Mail (iOS Simulator, arm64)**; **Build Wino Mail iOS solution** builds `WinoMail.iOS.slnx`. The workspace has no launch configuration yet: install and launch with the Simulator commands above.

## Verification

| Change | Required evidence |
| --- | --- |
| UIKit views, controls, navigation, or activation | iOS Debug Simulator build, plus manual checks on iPhone and iPad simulators |
| iOS platform services | iOS Debug Simulator build, plus manual checks of the affected service; sign-in, Keychain, notifications, and background refresh need a signed device build |
| Apple shared layer (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `Wino.Editor.WebKit`) | iOS Debug Simulator build and the Mac native Debug arm64 and x64 builds |
| StoreKit 2 Swift bridge | `./build-native.sh StoreKit2` and `plutil -p` showing the `macos`, `ios`, and `ios-simulator` slices ([bindings README](../../src/Wino.Core.Apple.Bindings/README.md)) |
| Signing, entitlements, `Info.plist`, or bundle identity | Signed device build, `codesign --verify`, and the effective entitlements and bundle identifier |
| Shared code used by the iOS app | The iOS build above, plus the Mac and Windows checks in [AGENTS.md](../../AGENTS.md) |

Manual checks on both idioms cover light and dark appearance, Dynamic Type, rotation, Split View and Stage Manager on iPad, the hardware keyboard, and VoiceOver. A build does not prove UI behavior. Do not upload to TestFlight to validate development changes.

## Pending external actions

These need the account holder and block device and sign-in checks:

- Done on 13 Oct 2026: the shared App ID `com.winomail.macos` (UNIVERSAL) has push and In-App Purchase; the iOS profiles **Wino Mail iOS Development** and **Wino Mail iOS App Store** (team `4VB7YWRAQ9`) are installed on the Mac. Add the iOS platform to the existing App Store record.
- Azure app registration: add the iOS redirect `msauth.com.winomail.macos://auth` for bundle `com.winomail.macos`.
- Google Cloud console: create an iOS OAuth client for `com.winomail.macos`. Then register an `IAuthenticatorConfig` that returns it as `GmailAuthenticatorClientId`, and replace the placeholder URL scheme in `Info.plist`. The desktop client ID does not accept a custom-scheme redirect.

## Release

There is no iOS release script yet. A later `scripts/release/ios/build-ios-appstore.sh` follows `build-macos-appstore.sh` ([macOS release guide](../releases-macos.md)).
