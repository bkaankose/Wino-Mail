# Wino Mail agent guidance

Wino Mail is a native mail, calendar, contacts, and To Do client with four applications in one repository:

| Platform | Application | Solution | Guide |
| --- | --- | --- | --- |
| Windows | `src/Wino.Mail.WinUI` (WinUI 3, MSIX, Native AOT) | `WinoMail.slnx` | [Windows development](docs/harness/development-windows.md) |
| macOS | `src/Wino.Mail.MacOS` (AppKit, CoreCLR) | `WinoMail.MacOS.slnx` | [macOS development](docs/harness/development-macos.md) |
| iOS and iPadOS | `src/Wino.Mail.iOS` (UIKit, Mono), UI projects `Wino.Mail.UIKit`, `.Phone`, `.Pad` | `WinoMail.iOS.slnx` | [iOS development](docs/harness/development-ios.md) |
| Android | `src/Wino.Mail.Android` (Android Views with XML layouts, AndroidX, Material, Mono) | `WinoMail.Android.slnx` | [Android development](docs/harness/development-android.md) |

All applications use the shared backend: `Wino.Core.Domain`, `Wino.Core`, `Wino.Services`, `Wino.Authentication`, `Wino.Messaging`, `Wino.Core.ML`, `Wino.Mail.ViewModels`, `controls/Wino.Mail.Controls.Core`, and `controls/Wino.Editor.Core`.
Platform services live in `Wino.Platform.Windows`, `Wino.Platform.MacOS`, `Wino.Platform.iOS`, and `Wino.Platform.Android`. The Apple apps share `Wino.Platform.Apple` (Foundation-level services: paths, configuration, Keychain, StoreKit, notifications), `Wino.Core.Apple.Bindings`, and `controls/Wino.Editor.WebKit`; `Wino.Platform.MacOS` and `Wino.Platform.iOS` hold only AppKit-only and UIKit-only services. Do not change the deprecated UWP project.

## Platform routing

- **Windows change** (WinUI head, Windows platform services, WinUI controls, XAML, MSIX, `build/Wino.Windows.*`, Windows scripts): read the [Windows guide](docs/harness/development-windows.md) before editing. Its Native AOT and build safety section is release-blocking.
- **macOS change** (AppKit head, Mac platform services, AppKit controls, entitlements, `build/Wino.MacOS.*`, Mac scripts): read the [macOS guide](docs/harness/development-macos.md).
- **iOS change** (iOS head, `Wino.Mail.UIKit*`, iOS platform services, UIKit controls, `Info.plist`, entitlements, `build/Wino.iOS.*`): read the [iOS guide](docs/harness/development-ios.md).
- **Android change** (Android head, `Wino.Platform.Android`, `controls/*.Android`, `AndroidManifest.xml`, Android resources and layouts, `build/Wino.Android.*`): read the [Android guide](docs/harness/development-android.md).
- **Apple shared change** (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `Wino.Editor.WebKit`): it affects the macOS and iOS applications. Read both Apple guides.
- **Shared change** (shared projects above, translations, `Directory.Build.*`, `Directory.Packages.props`, `global.json`): it affects all four applications. Read every guide's verification section, and keep shared code free of platform UI references.

## Scope and completion

- Start with the requested behavior and named files. Preserve unrelated worktree changes.
- Complete implementation and the applicable verification in the current task.
- If the user requests review only or prohibits launch, obey that boundary and report the remaining verification.
- After a passing check, repeat it only for changed inputs, new failures, or unresolved concerns.
- Report the result, verification evidence, and remaining limits. A build alone does not prove runtime behavior.
- Run checks on the available host. Report the other platform's exact pending checks as a handoff; do not claim cross-platform completion without that evidence.
- Never commit or push unless the user explicitly asks in the current request. Creating a local branch is fine; changes stay uncommitted on the local machine until the user asks for a commit or push.

## Shared coding rules

- Treat nullable diagnostics as real defects. Guard missing items and owners explicitly; two null account IDs comparing equal does not make either account safe to dereference. Do not hide errors with null-forgiving operators, warning suppressions, disabled analyzers, or weakened Release settings.
- Give collection expressions that target read-only interfaces an explicit concrete backing type. Collections that reach an items source need a concrete static type, not an interface; `WINOAOT001` enforces it. See the [implementation rules](docs/harness/implementation-rules.md#core-implementation-rules).
- Verify which source file the active project compiles before editing. Legacy and active files can share a namespace and type name.
- Keep business rules in shared services and ViewModels. Platform heads adapt them; they do not re-implement them.
- Add English strings only to `src/Wino.Core.Domain/Translations/en_US/resources.json`.
- Shared Release builds keep their Native AOT, trimming, and warnings-as-errors gates. Only projects that target `-macos`, `-ios`, or `-android` (the AppKit, UIKit, and Android projects and the Apple shared layer) are exempt from the AOT gate.
- `WinoTargetPlatform` (`Windows`, `MacOS`, `iOS`, or `Android`) and `WinoTargetRuntimeIdentifier` are the build interface. An explicit value wins; otherwise the host decides. The host never selects `iOS` or `Android`; those builds pass it explicitly. Platform build policy lives in `build/`; root `Directory.Build.*` holds only shared policy.

## Discovery and references

Use CodeGraph first for code discovery when `.codegraph/` exists:

```powershell
codegraph explore "named symbol or affected behavior"
```

If the result is unrelated or omits the requested script, use a targeted file read or `rg` search.
CodeGraph results can miss tests. Use the changed behavior to select additional checks.
Read only references that apply to the task:

| Task | Reference |
| --- | --- |
| C#, XAML, translations, storage, or editor changes | Relevant sections of [implementation rules](docs/harness/implementation-rules.md) |
| Windows build, deployment, F5, or runtime verification | [Windows development](docs/harness/development-windows.md) |
| macOS build, signing, F5, or runtime verification | [macOS development](docs/harness/development-macos.md) |
| iOS build, signing, Simulator, or runtime verification | [iOS development](docs/harness/development-ios.md) |
| Android build, emulators, sign-in registration, or runtime verification | [Android development](docs/harness/development-android.md) |
| New Windows UI feature or visual pattern | [Wino design guideline](docs/wino-design-guideline.md) |
| New macOS UI feature or visual pattern | [macOS design decisions](docs/macos-design-decisions.md), then the [Wino design guideline](docs/wino-design-guideline.md) for shared naming and order |
| New iOS or iPadOS UI feature or visual pattern | [iOS design decisions](docs/ios-design-decisions.md), then the [macOS design decisions](docs/macos-design-decisions.md) and the [Wino design guideline](docs/wino-design-guideline.md) |
| New Android UI feature or visual pattern | [Android design decisions](docs/android-design-decisions.md), then the [iOS design decisions](docs/ios-design-decisions.md) and the [Wino design guideline](docs/wino-design-guideline.md) |
| Moving duplicated Mac or iOS logic into shared code | [macOS shared-logic roadmap](docs/macos-shared-logic-roadmap.md) |
| Reusable controls or playground | [controls/AGENTS.md](controls/AGENTS.md) |
| Icons, icon fonts, or the colorful icon style | [icons/README.md](icons/README.md) and the Icons section of the [implementation rules](docs/harness/implementation-rules.md) |
| Intelligence jobs, artifacts, or the daily briefing | [mail intelligence](docs/mail-intelligence.md) |
| Release packaging | [release guide](docs/releases.md); Android in the [Android release guide](docs/releases-android.md) |
| What's New notes and illustrations | [whats-new skill](.claude/skills/whats-new/SKILL.md) |
| Scripts | [script index](scripts/README.md) |

Repository commands and package rules take precedence over stale personal skill instructions.
Use focused skills for the affected subsystem. Avoid loading overlapping general workflows for the same operation.

## Development loop

Use plain `dotnet`, `winapp`, and platform tools. Build and run commands restore dependencies by default; use the SDK's normal incremental behavior.
VS Code F5 on Windows uses the approved `scripts/development/windows/start-wino.ps1` helper to reuse unchanged Debug x64 output. This is the only development wrapper and input-hash cache. Keep custom debug build targets out of the project.
Mac F5 prepares the debugger, then runs a plain `dotnet build`. The iOS workspace has build tasks only; install and launch in the Simulator with `xcrun simctl`. The Android workspace has build and emulator tasks; deploy with `-t:Install` or `-t:Run`, never `adb install` of a Debug APK.

## Verification scope

| Change | Required evidence |
| --- | --- |
| Documentation or tooling | Valid links, command configuration, and affected script checks |
| Domain or service logic | Affected project build and directly affected unit tests |
| ViewModel behavior | Affected unit tests and manual checks for UI behavior on each affected platform |
| Windows XAML, navigation, activation, windows, or controls | Windows Debug build and manual checks in the lab |
| AppKit views, navigation, activation, or controls | Native Debug arm64 and x64 builds and manual checks on the Mac |
| UIKit views, navigation, activation, or controls | iOS Debug Simulator build and manual checks on iPhone and iPad simulators |
| Android views, navigation, activation, or controls | Android Debug build and manual checks on the phone and tablet emulators |
| Apple shared layer (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `Wino.Editor.WebKit`) | Mac native Debug arm64 and x64 builds and the iOS Debug Simulator build |
| Localization | English source only, generated output build, other locales untouched |
| Windows package, trimming, or Native AOT | Windows Release build without launch, plus release-script tests when applicable |
| macOS signing, entitlements, or release inputs | Native Release arm64 and x64 builds and bundle identity, signature, and entitlement checks |
| iOS signing, entitlements, or `Info.plist` | Signed device build and bundle identity, signature, and entitlement checks |
| Android release inputs (manifest, signing, trimming, Mono AOT) | Release Mono AOT `android-arm64` build and the 16 KB page-size check |
| Shared production code, dependencies, or build configuration | Windows Debug x64 build and Release x64 gate, affected tests, the Android Debug build, and the macOS and iOS builds as a handoff when no Mac is available |

Windows production changes always need the Release gate, run without launching or deploying:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
```

Format changed XAML before building with `scripts/maintenance/windows/format-xaml.ps1 -Changed`, and run it with `-Check` before handoff.
Keep unit tests and release-script tests. Agent-driven UI automation is retired. The [local Docker lab](tools/local-lab/README.md) is the Windows application testing environment; use manual interaction for UI checks.
A passing build does not prove UI behavior. Report manual verification as pending unless someone performed it.

## Accounts, OAuth clients and signing (hard rules)

Binding for every agent. Backend counterpart: `AGENTS.md` and `docs/social-sign-in-setup.md` in Wino-Mail-API.

- **Never touch the Google UWP OAuth client `973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn`.** It is the Windows
  app's Gmail mailbox client. Do not change, rotate, replace or repurpose it, and never use it for Wino Accounts
  sign-in. Ask the user if a task seems to need it.
- Wino Accounts Google sign-in: Windows uses the API's browser flow (`/api/v1/auth/google/authorize`); iOS and
  macOS use the iOS client `973025879644-qlc58h7f2gepqgu3ocvlr7mqvd3fff1c` (`WinoAccountExternalAuth.GoogleAppleClientId`,
  an `ASWebAuthenticationSession` with PKCE in `GoogleWebSessionSignInProvider`) and post the ID token to
  `/api/v1/auth/google/native`; Android uses Credential Manager (`AndroidGoogleSignInProvider`) with `serverClientId`
  = the web client `973025879644-92h0mdbvgaiq0p6efdv16hpiop65er6a.apps.googleusercontent.com`
  (`WinoAccountExternalAuth.GoogleAndroidServerClientId`). The Android client ids are never named in code; Google
  matches package `com.winomail.android` and the signing certificate.
- Apple: iOS and macOS share App ID `com.winomail.macos` (team `4VB7YWRAQ9`) with Sign in with Apple enabled. The
  iOS entitlements files and `Wino.Mail.MacOS/Entitlements.AppStore.plist` carry `com.apple.developer.applesignin`;
  macOS development builds get it through `CustomEntitlements` when a profile signs the build.
- Developer ID profiles cannot carry Sign in with Apple. `build-macos-release.sh` passes `-p:WinoAppleSignIn=false`;
  never remove that or add the entitlement to DMG builds, or macOS refuses to launch the app. DMG users sign in
  with Apple through the API's browser flow. The Mac csproj defines `WINO_APPLE_SIGNIN` under exactly the
  entitlement condition; `Composition` registers the native `AppleNativeSignInProvider` with it, and without it the
  shared `WinoAccountExternalSignInService` falls back to the `ASWebAuthenticationSession` browser flow. Android
  always uses the browser flow for Apple.
- The browser flow's callback address (`wino-mail://auth-complete`, the API's `Secrets:ExternalAuth:AppCallbackUri`)
  is spelled once on the client, in `WinoAccountExternalAuth` (`Wino.Core.Domain`); the Android intent filter and
  the Mac web session read it from there. Change it there and in the API together.
- Provisioning profiles are selected by name ("Wino Mail iOS Development", "Wino Mail iOS App Store", "Wino Mail
  macOS App Store", "Wino Mail macOS Developer ID", "Wino Mail macOS Development"). Recreating a profile must keep
  its exact name. Changing App ID capabilities invalidates all of them.
- Build Apple targets with `~/.dotnet/dotnet` (it has the ios/macos/android workloads; `/usr/local/share/dotnet`
  does not). Code signing fails over SSH (`errSecInternalComponent`: the login keychain is locked per SSH
  session), so signed builds run in a local Terminal.

## Dependencies

All applications share `Directory.Packages.props`, `global.json`, and `nuget.config`.
Published cross-repository dependencies must use unconditional `PackageReference` items.
If a dependency change requires publication, audit and publish the version, then update every consumer.
Publish without asking for approval. When `NUGET_API_KEY` is set in the local environment, pack and push the package to nuget.org yourself. This does not need a git commit or push.
Never substitute a local feed, local package reference, or sibling project reference.
Wait until nuget.org indexes the version before you restore consumers.
Never print or log the key.
