# G: iOS build interface (T12) and Mac preparation

Worktree: `D:\Wino-Mail\.claude\worktrees\agent-a49564a0fc5c19085` (branch `worktree-agent-a49564a0fc5c19085`, uncommitted).
Changed: `Directory.Build.targets`, new `build/Wino.iOS.targets`, one sentence in `docs/harness/development-macos.md`. No csproj, source, or `Directory.Build.props` change.

## Diff

```diff
--- a/Directory.Build.targets
+++ b/Directory.Build.targets
-  <PropertyGroup Condition="... and '$(IsTestProject)' != 'true' and !$(TargetFramework.Contains('-macos'))">
+  <PropertyGroup Condition="... and '$(IsTestProject)' != 'true' and !$(TargetFramework.Contains('-macos')) and !$(TargetFramework.Contains('-ios'))">
   <Import Project="build/Wino.MacOS.targets" Condition="'$(WinoTargetPlatform)' == 'MacOS' or $(TargetFramework.Contains('-macos'))" />
+  <Import Project="build/Wino.iOS.targets" Condition="'$(WinoTargetPlatform)' == 'iOS' or $(TargetFramework.Contains('-ios'))" />
-    <Error Condition="'$(WinoTargetPlatform)' != '' and '$(WinoTargetPlatform)' != 'Windows' and '$(WinoTargetPlatform)' != 'MacOS'"
-           Text="WinoTargetPlatform must be Windows or MacOS, not '$(WinoTargetPlatform)'." />
+    <Error Condition="'$(WinoTargetPlatform)' != '' and '$(WinoTargetPlatform)' != 'Windows' and '$(WinoTargetPlatform)' != 'MacOS' and '$(WinoTargetPlatform)' != 'iOS'"
+           Text="WinoTargetPlatform must be Windows, MacOS, or iOS, not '$(WinoTargetPlatform)'." />
```

`build/Wino.iOS.targets` (new):

- For `-ios` TFMs: `IsAotCompatible=false`, `PublishAot=false`.
- For `-ios` TFMs: `SupportedOSPlatformVersion=17.0` when it is empty or equal to `TargetPlatformVersion`. The .NET SDK fills an unset value with `TargetPlatformVersion` before Directory.Build.targets is evaluated. An explicit value such as 18.0 or 15.0 is kept, and both cases were verified.
- **RuntimeIdentifier is not defaulted (this departs from the brief).** The iOS SDK sets its default RID in `Xamarin.Shared.Sdk.props`. It does so before Directory.Build.targets is imported, which I confirmed with `-pp` import order. In the same file it derives `_SdkIsSimulator`/`SdkIsDevice`, and `RuntimeIdentifierInference.targets` also runs before this file. If this file overrode the RID, those values would be left stale. For example, Release would get RID `ios-arm64` while `_SdkIsSimulator` stayed `true`. SDK behaviour:
  - Libraries stay portable, with no RID.
  - Exe `build` uses `iossimulator-arm64` on Apple Silicon for both Debug and Release.
  - Exe `publish` uses `ios-arm64`.
- Trimming (`MtouchLink`/`TrimMode`) is left at the SDK defaults. `Xamarin.Shared.Sdk.Trimming.props` is evaluated after this file through `CustomAfterDirectoryBuildTargets`, and it gives `_DefaultLinkMode=SdkOnly` for both simulator arm64 and device.

Docs: added this sentence under "Platform selection" in `development-macos.md`: "iOS builds on the same host pass `-p:WinoTargetPlatform=iOS` explicitly; the host never selects it. Its policy is in `build/Wino.iOS.targets`."

## Mac (burak-mac)

- `~/.dotnet/dotnet workload install ios --skip-manifest-update` was a user-level install with no elevation. `--skip-manifest-update` kept macos at the same manifest.
- `dotnet workload list` (workload version 10.0.401.1, SDK 10.0.401):
  - `ios 27.0.10722/10.0.100`
  - `macos 27.0.10722/10.0.100`
  - `maui 10.0.110`
  - `maui-maccatalyst 10.0.110`
- Packs: `Microsoft.iOS.Sdk.net10.0_27.0` (27.0.10722), the Mono runtime packs for ios-arm64 and iossimulator-arm64/x64, and the AOT cross compilers. `Microsoft.iOS.Sdk.net10.0_26.0` (26.0.11017) was already present.
- Xcode 27.0 (27A266a). `xcodebuild -showsdks`: `iphoneos27.0` and `iphonesimulator27.0`.
- **Missing: there are no iOS Simulator runtimes or devices.** `xcrun simctl list runtimes` is empty. The Phase 2–3 simulator launch needs `xcodebuild -downloadPlatform iOS`, a download of several GB. I did not run it.

## -getProperty (Wino.Core.Domain, Release)

Windows: main checkout `D:\Wino-Mail` (before) compared with the worktree (after). Output was identical.

| Platform | WinoTargetPlatform | IsAotCompatible | TreatWarningsAsErrors | RuntimeIdentifiers |
| --- | --- | --- | --- | --- |
| iOS | iOS | true | true | |
| MacOS | MacOS | true | true | |
| Windows | Windows | true | true | win-x86;win-x64;win-arm64 |

Mac: `~/Desktop/Wino-Mail` (before, at commit 69f0efa8; its build files are CR-insensitive identical to the mirror apart from this change) compared with the `~/Desktop/wt-G` mirror (after). The values match the table above. The host default still resolves `MacOS` in both.

Validation runs the `WinoValidateTargetPlatform` target. `-getProperty` only evaluates, so it prints `Linux` without an error.

- Linux:
  - Before: error `must be Windows or MacOS, not 'Linux'`.
  - After: error `must be Windows, MacOS, or iOS, not 'Linux'`.
- iOS:
  - Before: error `... not 'iOS'`.
  - After: passes.

Byte-level equivalence: I diffed `-pp` preprocessed output for Wino.Core.Domain, main against the worktree, for Windows and MacOS in both Debug and Release. The only differences are the three edited lines, and the iOS import appears as not taken (commented out).

## Probe (`~/Desktop/wt-G/scratch`, deleted afterwards)

`net10.0-ios` classlib, `-p:WinoTargetPlatform=iOS`:

- `dotnet build -c Debug` and `-c Release` both succeeded with 0 warnings and 0 errors.
- `IsAotCompatible=false`, `PublishAot=false`, `SupportedOSPlatformVersion=17.0`, `RuntimeIdentifier=` (portable), `TreatWarningsAsErrors=false`, `TrimMode=partial`.
- The generated AssemblyInfo contains `SupportedOSPlatformAttribute("iOS17.0")`.
- The same values result without `WinoTargetPlatform`, because the import is TFM-driven.

The same probe evaluated as Exe:

- Debug and Release builds: RID `iossimulator-arm64`, `_SdkIsSimulator=true`, TargetPlatformVersion 27.0.
- Publish: RID `ios-arm64`, `_SdkIsSimulator=false`.
- Exe with `<RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == '' and '$(Configuration)' == 'Release'">ios-arm64</RuntimeIdentifier>` in the project body: Release gives `ios-arm64`, `_SdkIsSimulator=false`, `SdkIsDevice=true`. Debug gives `iossimulator-arm64`.

The `dotnet new ios` app with `-p:WinoTargetPlatform=iOS`, Debug, built `bin/Debug/net10.0-ios/iossimulator-arm64/IosApp.app` with 0 warnings and 0 errors. That proves the workload and Xcode 27 toolchain work. The template's explicit `SupportedOSPlatformVersion` 15.0 was kept.

## Windows regression

`dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false` in the worktree succeeded with 0 warnings and 0 errors.

Logs are in `D:\Wino-Mail\artifacts\ios-platform\`: `G-windows-release-x64.log`, `G-eval-windows.log`, `G-eval-mac.log`, `G-probe-mac.log`.

## What the iOS head (Phase 3) must set itself

- `<RuntimeIdentifier Condition="'$(RuntimeIdentifier)' == '' and '$(Configuration)' == 'Release'">ios-arm64</RuntimeIdentifier>` in the csproj body, if Release `build` should target the device. Otherwise pass `-r`.
- `SupportedOSPlatformVersion` 17.0 comes from `build/Wino.iOS.targets`. The head may still state it explicitly, and Info.plist `MinimumOSVersion` (if present) must match it.
- The TFM `net10.0-ios` resolves to TargetPlatformVersion 26.0 for libraries and 27.0 for app projects on this workload. To pin one SDK, use `net10.0-ios27.0` (or the same version across head and libraries).
- `-ios` projects drop out of the Release gate, which includes `TreatWarningsAsErrors`; this is the same as `-macos`. If the head and UIKit libraries want warnings as errors, they set it themselves.
- Bundle identity, signing, entitlements, `UseInterpreter`/`MtouchInterpreter` for simulator Debug (the SDK default leaves both empty and gives `SdkOnly` linking on simulator arm64), and `ApplicationId com.winomail.ios`.
- Install a simulator runtime on the Mac before the first launch (`xcodebuild -downloadPlatform iOS`).
