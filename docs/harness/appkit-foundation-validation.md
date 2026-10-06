# AppKit foundation validation

The accepted scope is the platform extraction and native AppKit foundation, with OAuth onboarding and real folder navigation. This is not the full Mac feature port. The production identity is `com.winomail.desktop`; App Sandbox is enabled from the first production build. Existing experimental data is preserved without migration.

## Source and project boundaries

- `WinoMail.slnx` remains the Windows entry point. `WinoMail.MacOS.slnx` contains the native head/platform/controls and portable dependencies. Pass `-p:WinoTargetPlatform=MacOS` when building the Mac solution.
- Active Mail, Calendar, Common and Shell ViewModels live in `Wino.Mail.ViewModels`. The default two targets remain portable `net10.0` and Windows `net10.0-windows10.0.26100.0`; Mac selection builds only the portable target. Frozen legacy VM projects support the untouched deprecated UWP host.
- Windows platform implementations live in `Wino.Platform.Windows` or the WinUI head. Mac implementations live in `Wino.Platform.MacOS` or the AppKit head. Shared code does not choose Windows platform implementations.
- WinUI controls/editor/playground assembly names explicitly include WinUI. CLR namespaces remain compatible. `Wino.Editor.Core` owns the single canonical editor web assets and shared session/security contracts.
- `Directory.Build.props` uses the canonical case-sensitive filename. Windows Release RID defaults preserve explicit Mac RIDs. The AppKit head uses the workload managed runtime; Windows/shared Release AOT analysis remains enabled.

## Windows verification

- WinUI Debug x64: passed, 0 warnings, 0 errors (`artifacts/appkit-windows-debug.log`).
- WinUI Release x64: passed, 0 warnings, 0 errors; MSIX output generated without deployment or launch (`artifacts/appkit-windows-release.log`). The coordinating Windows chat also reported the final incremental check passed in 2:09 with 0 warnings and 0 errors (`artifacts/appkit-windows-release-final.log`).
- ViewModel suite: 90/90 passed; final affected lifecycle follow-up passed 6/6. Notification capability/runtime follow-up passed 9/9.
- Collection/editor boundary tests: 31 passed. Binding lifetime tests: 3 passed.
- Shared foundation/auth/recovery/runtime/Store checks and affected tool builds: see [import manifest](platform-import-manifest.md).
- Renamed controls/editor Debug builds: passed without warnings. Renamed playground Debug build: passed with 20 existing WMC1510 binding warnings.
- Release script syntax/profile/argument/target checks: passed; no tracked standalone release-script test suite exists.
- XAML formatting check: 27/27 passed. Shared project graph inspection: no native UI/reverse platform references. Only the English translation source changed.

## Native verification

Build on the Mac with the .NET macOS workload and its matching Xcode version:

```sh
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-x64 -p:WinoTargetRuntimeIdentifier=osx-x64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
```

Verified on 2026-10-07 on Apple Silicon, macOS 26.7, .NET SDK 10.0.401/runtime 10.0.12, macOS workload 27.0.10722, and Xcode 27.0 (27A266a), selected at `/Applications/Xcode-27.0.0.app/Contents/Developer`:

- Native Debug arm64: passed, 17 warnings, 0 errors, 16.55 seconds (`artifacts/appkit-macos-debug-arm64.log`). Native Debug x64: passed, 9 warnings, 0 errors, 5.44 seconds (`artifacts/appkit-macos-debug-x64.log`). Warnings cover credential nullability, obsolete AppKit APIs, a hidden member and an unused parameter; these remain open cleanup items.
- `dotnet build WinoMail.MacOS.slnx -c Debug -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true`: passed, 0 warnings, 0 errors (`artifacts/appkit-macos-solution.log`).
- Targeted `MagikaContentTypeClassificationModelTests`: 10/10 passed on Apple Silicon (`artifacts/appkit-macos-ml-tests.log`). Physical Intel runtime behavior remains unverified.
- Pass `WinoTargetRuntimeIdentifier` globally with the native RID. The SDK clears `RuntimeIdentifier` on portable reference builds; the explicit selector keeps Intel ML restore and compile consistent. Intel retains the existing unavailable ML implementation; Apple Silicon retains ONNX. Existing Windows architecture conditions remain unchanged; the coordinating Windows chat must rerun its affected Release check after importing this selector change.
- Workload output is `src/Wino.Mail.MacOS/bin/Debug/net10.0-macos/<RID>/Wino Mail.app/Contents/MacOS/Wino.Mail.MacOS`. VS Code now uses this actual arm64 bundle path. The arm64 build task uses the explicit runtime selector and preserves Windows tasks/configurations. JSON configuration parsing passed; production F5/breakpoint acceptance remains pending.
- Built Info.plist has `CFBundleIdentifier=com.winomail.desktop`. Effective arm64 entitlements contain App Sandbox, network client/server, user-selected file read/write and JIT. `codesign --verify --deep --strict` passed for both bundles; the Intel launcher is Mach-O x86_64. Ad-hoc development signing is not distribution signing/notarization evidence.
- Launched the production arm64 `.app` with `open`; the native Welcome page appeared. First launch exposed missing `ILogger<WinoTelemetryService>` registration; the Mac composition now registers logging through Serilog. Failed startup quit previously attempted to stop an uninitialized runtime; shutdown now stops it only after successful startup. The application delegate is retained across the AppKit run loop, and Welcome window delegates have managed owners.
- Startup logs subsequently recorded initialized synchronization dependencies and Welcome appeared without a startup error. The three recorded error entries came from the earlier failed startup and its two quit attempts. Production data is under `~/Library/Containers/com.winomail.desktop/Data/Library/Application Support/com.winomail.desktop`; preferences, database and log files were created there. No data/credential deletion or sandbox bypass was used. Restart showed Welcome; no OAuth account persistence claim is made.
- The app remains running for the user's manual OAuth/folder test. That outcome is pending and must be recorded separately. No UI automation scripts were created or run.
- CodeGraph 1.6.2 CLI is installed and usable on this Mac. Repository synchronization completed with 5,659 files, 109,137 nodes and 201,195 edges; its database is ignored local output.

## Manual acceptance and next packages

Manual Windows lab acceptance remains pending: credentials/login/refresh, retained preferences/database/MIME, startup/sync/account removal, navigation, reader/drafts/attachments, printing/PDF/S-MIME, notifications/activation, tray/reopen/quit and Store behavior.

For the requested Mac foundation, manually verify Welcome -> provider -> browser OAuth -> persisted account -> native account/folder sidebar; account restart/refresh; cancellation/retry; close/reopen/quit during pending work; sandbox container paths; and Keychain access denied/missing/restart behavior. IMAP configuration is deferred by the user. Source or compile evidence cannot establish these outcomes.

Full parity is tracked in [the Windows source inventory](appkit-parity-inventory.csv). Its proposed Mac paths identify future work; they are not evidence that a file exists or a feature is accepted. Source-present rows still require native build and manual verification. Remaining packages include reader/editor/compose/send, remaining settings/calendar/contacts/tasks dialogs, native notification actions/reminders and Dock behavior, startup/tray equivalents, menus/accessibility/IME, print/PDF, S-MIME, additional windows, general activation/global hotkeys, and Apple distribution/minimum-OS/physical Intel validation. Microsoft Store UI is the explicit exclusion; Wino Account billing stays shared.

The bidirectional [donor comparison](appkit-donor-comparison.csv) records normalized source/hash comparisons against donor 62858db and destination 40bae46a at import start. Imported changes are selective; current destination-only recovery and consent behavior is retained. Other locales were not imported. Keychain abandoned credential revisions require a later committed-reference reconciliation audit; failed database writes retain prior usable revisions.
