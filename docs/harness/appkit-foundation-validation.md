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
- WinUI Release x64: passed, 0 warnings, 0 errors; MSIX output generated without deployment or launch (`artifacts/appkit-windows-release.log`). The coordinating Windows chat also reported the final incremental check passed in 2:09 with 0 warnings and 0 errors (`artifacts/appkit-windows-release-final.log`). Its Release check after importing Mac integration commit `01ac2770` passed in 2:39 with 0 warnings and 0 errors (`artifacts/appkit-windows-release-mac-integrated.log`).
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
- The user manually verified Outlook OAuth and setup synchronization, but reported no transition to the shell and a null-value error on restart. The follow-up and remaining manual acceptance are recorded below. No UI automation scripts were created or run.
- CodeGraph 1.6.2 CLI is installed and usable on this Mac. Repository synchronization completed with 5,659 files, 109,137 nodes and 201,195 edges; its database is ignored local output.

## Manual acceptance and next packages

Manual Windows lab acceptance remains pending: credentials/login/refresh, retained preferences/database/MIME, startup/sync/account removal, navigation, reader/drafts/attachments, printing/PDF/S-MIME, notifications/activation, tray/reopen/quit and Store behavior.

For the requested Mac foundation, manually verify Welcome -> provider -> browser OAuth -> persisted account -> native account/folder sidebar; account restart/refresh; cancellation/retry; close/reopen/quit during pending work; sandbox container paths; and Keychain access denied/missing/restart behavior. IMAP configuration is deferred by the user. Source or compile evidence cannot establish these outcomes.

Full parity is tracked in [the Windows source inventory](appkit-parity-inventory.csv). Its proposed Mac paths identify future work; they are not evidence that a file exists or a feature is accepted. Source-present rows still require native build and manual verification. Remaining packages include reader/editor/compose/send, remaining settings/calendar/contacts/tasks dialogs, native notification actions/reminders and Dock behavior, startup/tray equivalents, menus/accessibility/IME, print/PDF, S-MIME, additional windows, general activation/global hotkeys, and Apple distribution/minimum-OS/physical Intel validation. Microsoft Store UI is the explicit exclusion; Wino Account billing stays shared.

The bidirectional [donor comparison](appkit-donor-comparison.csv) records normalized source/hash comparisons against donor 62858db and destination 40bae46a at import start. Imported changes are selective; current destination-only recovery and consent behavior is retained. Other locales were not imported. Keychain abandoned credential revisions require a later committed-reference reconciliation audit; failed database writes retain prior usable revisions.


## Persisted-account shell follow-up (2026-10-07)

The user reported Outlook OAuth and setup synchronization succeeded, but the shell did not appear; restarting displayed “Value cannot be null”. The sandbox log identified a handled managed startup exception, rather than an OS crash. No matching production native crash report was present in DiagnosticReports.

```text
System.ArgumentNullException: Value cannot be null. (Parameter 'value')
  at ObjCRuntime.ThrowHelper.ThrowArgumentNullException(String argumentName)
  at ObjCRuntime.NativeObjectExtensions.GetNonNullHandle(INativeObject self, String argumentName)
  at AppKit.NSWindow.set_ContentViewController(NSViewController value)
  at Wino.Mail.MacOS.AppDelegate.HostController(NSViewController controller) [pre-fix line 61]
  at Wino.Mail.MacOS.Infrastructure.AppKitNavigationService.<ShowShellAsync>b__1() [pre-fix line 110]
  at Wino.Mail.MacOS.Infrastructure.AppKitDispatcher.ExecuteOnUIThread(Action action) [line 14]
  at Wino.Mail.MacOS.Infrastructure.AppKitNavigationService.ShowShellAsync() [pre-fix line 110]
```

The native host assigned null to the binding's non-null `ContentViewController` setter when switching from Welcome/loading to the shell. This path is shared by onboarding completion and saved-account startup. The Mac host now retains the old window/controller until the router awaits controller release, then closes and disposes that window. The replacement window is shown before publication. The router publishes shell/current route state only after hosting succeeds and disposes an unhosted candidate on failure, so a failed host cannot permanently poison its shell state. No shared ViewModel changes were needed.

Native startup verification after the fix loaded the persisted Outlook account into the real shell: account and Inbox, Sent Items, Drafts, Archive, Deleted Items and Junk Email rows were visible, and Inbox messages were populated. The sandbox log after that startup contained no error/fatal entries. Saved account/database/Keychain data was preserved. This establishes saved-account startup and visible real folders; the user must repeat the first-run OAuth-to-shell transition and manually verify folder selection, restart/refresh and cancellation/retry. Host-failure recovery is source/build verified, without injected native failure or UI automation scripts.

Follow-up build logs are `artifacts/appkit-macos-shell-fix-arm64.log` and `artifacts/appkit-macos-shell-fix-x64.log`. Final native Debug arm64 passed with 17 warnings/0 errors in 16.88 seconds; x64 passed with 9 warnings/0 errors in 5.08 seconds. `codesign --verify --deep --strict` passed for the rebuilt arm64 bundle. The current app is left open for manual folder switching and quit/relaunch acceptance; the final window-retirement ordering was build checked after the visible-folder check and needs that manual restart check.

Production Debug evaluates `UseMonoRuntime=false`, and its bundle contains `libcoreclr.dylib`, `libclrjit.dylib` and `libclrgc.dylib`. VS Code `type: coreclr` and the native bundle launcher are compatible with that runtime. At the original handoff, production F5/breakpoint behavior had not been exercised; the earlier breakpoint evidence applied only to the PoC. The user subsequently requested its removal; the follow-up below supersedes that retirement gate.

## User handoff boundary (2026-10-07)

The user confirmed that the app lists real folders correctly, then rejected visual parity: an extra About button in Shell, missing folder icons and Welcome brand artwork, missing New Mail, vertically misaligned folder labels, editable-looking folder labels whose changes are ignored, and incomplete About content. These are open UI defects/deviations, not accepted platform adaptations. The accepted D6 presentation requirement remains in force.

The user explicitly stopped UI implementation and planning in this task and will start a separate planning task later. No UI fixes were made after that boundary. The next task should retain the user's decision to restore the New Mail button with an explicit disabled state while compose remains deferred. No new UI plan is delivered by this task.

The non-UI foundation is committed and pushed: platform service extraction, shared ViewModel graph, Windows adapters, editor/core ownership, AppKit project/runtime foundation, sandbox identity, credential adapters and native build integration. Windows Release after shared Mac build-selector integration passed with 0 warnings/0 errors. Subsequent source changes were Mac-only shell/window lifetime fixes; Windows build inputs did not change. Mac arm64/x64 builds, solution build and 10 ML tests passed. Effective development sandbox entitlements and signatures were checked. Outlook OAuth and setup sync were manually confirmed; saved-account shell/folder startup was verified after its null-setter fix. Google OAuth, fresh OAuth-to-Shell after the fix, folder switching, safe quit/relaunch, Keychain denial/loss and other manual scenarios are not claimed accepted.

At that handoff, native Debug warning cleanup, full visual/interaction parity, production F5/breakpoint acceptance and PoC retirement remained future work. Existing account/database/Keychain data was preserved. Windows manual lab acceptance remained pending; no Windows deployment/launch had been performed. The current implementation is a working infrastructure prototype, not a completed full-parity Mac port.

## PoC retirement and development follow-up (2026-10-07)

The user requested removal of the PoC, a lab build without launching it, and working F5 configurations for both production platforms. UI parity work remains stopped. Both PoC projects and their sample source were removed; active solutions, project references and VS Code configurations contain no PoC references. Historical experiment descriptions remain as provenance.

`dotnet build tools/local-lab/DatabaseGenerator/DatabaseGenerator.csproj -c Debug -p:Platform=x64` passed with 0 warnings and 0 errors (`artifacts/appkit-lab-retirement-build.log`). The lab PowerShell entry point passed syntax parsing. Docker CLI is unavailable on this Windows host, so Compose validation was not performed. No lab containers, provisioning, seeding or database generation were launched; Windows manual lab acceptance remains pending.

Windows F5 initially failed with APPX1101: both AnyCPU and x64 editor-core PDBs entered the same package destination. Project-reference metadata previously updated only direct references already declared at evaluation time. The SDK later added a transitive editor-core reference without those defaults. The WinUI head now supplies item-definition defaults for direct and transitive references, including `SetPlatform` used by packaging. Evaluated project-only graphs, including WinApp's solution parameters, select x64 consistently. The project-only Debug build passed with 0 warnings/errors (`artifacts/appkit-f5-packaging-debug.log`). Existing AnyCPU output was retained, so this check did not depend on deleting the conflicting build output.

The actual checked-in **Debug Wino Mail (packaged, x64)** configuration was exercised through VS Code's debugger API. Its approved helper/prelaunch task exited 0, packaged activation succeeded, and `coreclr` attached. A SHA256-checked source breakpoint bound and hit `Program.OnActivated` at `src/Wino.Mail.WinUI/Program.cs:307` after a supported AppsFolder activation. The managed stack identified the same method and line. The process continued and the debugger detached (`artifacts/f5/vscode-verification6/events.jsonl`). Installed identity/publisher matched the manifest and the package was a development installation. Account data was preserved. This proves packaged F5 and a managed activation breakpoint; it does not establish Windows lab/UI acceptance or startup breakpoints before attach.

After the reference-metadata fix, WinUI Release x64 passed with 0 warnings/errors in 2:41.44 and produced MSIX and symbol packages (`artifacts/appkit-f5-packaging-release.log`). Release was not deployed or launched. No release script or target changed in this follow-up.

Mac production F5 passed on `mac.local` with C# extension 2.160.4 and its matching debugger 18.12.10903.2. The original bundled diagnostic shim could not complete the sandboxed launch handshake. The maintenance task prepares an isolated copy of the installed extension's debugger with Microsoft's checksum-pinned `Microsoft.Diagnostics.DbgShim.osx-arm64` 10.0.745401 shim, leaving the extension unchanged. Supported pipe transport and app launch environments use the same sandbox container `TMPDIR`. The final Mac workspace/configuration automatically runs that setup before the plain native build. The workspace selects the separate Mac solution while preserving the Windows folder default.

Computer-use verification hit `Program.cs:9`, stepped to line 10, and hit `AppDelegate.StartAsync` at `AppDelegate.cs:38` with locals and managed frames. Continuing opened the saved mailbox/folder tree. The final exact-source configuration repeated the startup breakpoint, stepping and saved-shell continuation; normal quit passed. Native evidence is `artifacts/appkit-macos-f5-acceptance.txt` on the Mac and the breakpoint screenshot in the remote chat. App Sandbox remained enabled, signature verification passed, and account/database/Keychain references were preserved. Setup syntax and cached/idempotent execution passed. The workaround's version pin and repair/rollback instructions are in [development commands](development.md#native-macos-f5); extension updates require renewed breakpoint verification. Physical Intel debugging and Remote SSH debugging are not claimed.
