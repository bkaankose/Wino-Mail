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
- WinUI Release x64: passed, 0 warnings, 0 errors; MSIX output generated without deployment or launch (`artifacts/appkit-windows-release.log`). A final incremental check after whitespace cleanup is recorded in `artifacts/appkit-windows-release-final.log`.
- ViewModel suite: 90/90 passed; final affected lifecycle follow-up passed 6/6. Notification capability/runtime follow-up passed 9/9.
- Collection/editor boundary tests: 31 passed. Binding lifetime tests: 3 passed.
- Shared foundation/auth/recovery/runtime/Store checks and affected tool builds: see [import manifest](platform-import-manifest.md).
- Renamed controls/editor Debug builds: passed without warnings. Renamed playground Debug build: passed with 20 existing WMC1510 binding warnings.
- Release script syntax/profile/argument/target checks: passed; no tracked standalone release-script test suite exists.
- XAML formatting check: 27/27 passed. Shared project graph inspection: no native UI/reverse platform references. Only the English translation source changed.

## Native verification

Build on the Mac with the .NET macOS workload and its matching Xcode version:

```sh
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-x64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
```

Native compiler, signed entitlements, bundle launch and persistence checks are pending the remote build. The launch/debug output path must be verified against the workload output. Ad-hoc development signing is not distribution signing/notarization evidence.

## Manual acceptance and next packages

Manual Windows lab acceptance remains pending: credentials/login/refresh, retained preferences/database/MIME, startup/sync/account removal, navigation, reader/drafts/attachments, printing/PDF/S-MIME, notifications/activation, tray/reopen/quit and Store behavior.

For the requested Mac foundation, manually verify Welcome -> provider -> browser OAuth -> persisted account -> native account/folder sidebar; account restart/refresh; cancellation/retry; close/reopen/quit during pending work; sandbox container paths; and Keychain access denied/missing/restart behavior. IMAP configuration is deferred by the user. Source or compile evidence cannot establish these outcomes.

Full parity is tracked in [the Windows source inventory](appkit-parity-inventory.csv). Its proposed Mac paths identify future work; they are not evidence that a file exists or a feature is accepted. Source-present rows still require native build and manual verification. Remaining packages include reader/editor/compose/send, remaining settings/calendar/contacts/tasks dialogs, native notification actions/reminders and Dock behavior, startup/tray equivalents, menus/accessibility/IME, print/PDF, S-MIME, additional windows, general activation/global hotkeys, and Apple distribution/minimum-OS/physical Intel validation. Microsoft Store UI is the explicit exclusion; Wino Account billing stays shared.

The bidirectional [donor comparison](appkit-donor-comparison.csv) records normalized source/hash comparisons against donor 62858db and destination 40bae46a at import start. Imported changes are selective; current destination-only recovery and consent behavior is retained. Other locales were not imported. Keychain abandoned credential revisions require a later committed-reference reconciliation audit; failed database writes retain prior usable revisions.
