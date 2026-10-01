# Debug deployment investigation

Date: 2026-10-01. Host CLI: WinApp 0.7.0. SDK: .NET 10.0.301.
The findings describe the original behavior. Implemented changes and verification follow.

## Findings

### Local access denied: notification hosts locked the package layout

The local failure was reproducible without compilation:

```powershell
winapp run . --project Wino.Mail.WinUI --no-build --detach --json
```

WinApp returned `UnauthorizedAccess_IODenied_NoPathName`.
Verbose output stopped after `Using appxrecipe for layout: Wino.Mail.WinUI.build.appxrecipe`.
The failure occurred before package activation.

Stopping the main app did not release every package file.
There were 39 surviving `Wino.Mail.NotificationHost.exe` processes.
Their command lines identified the checked-in Debug registration's `AppX` directory.
A write-access probe on `AppX/Wino.Mail.NotificationHost.exe` reported that another process held the file.
The build recipe required replacement of the host executable and other host payload files.

After doctor passed, the investigation force-stopped only the 39 verified Debug hosts.
The same no-build command then succeeded with PID `76612` and the existing package identity.
No identity change, package cleanup, or data deletion was necessary.

This is a package-layout file lock, rather than a failure inside Wino's main activation code.
WinApp 0.7.0 copies recipe files with `AtomicFile.Copy` before registration and activation.
The generic error hides the destination that failed.

The reason those hosts survived remains a separate lifecycle question.
Their command lines contained `--request`, rather than notification activation-bridge arguments.
`NotificationHostRuntime.ProcessRequest` synchronously waits for notification removal operations.
`NotificationHostClient.DispatchAsync` discards the returned process ID and does not observe completion.
These are investigation points, not proof of the particular wait that held each process.

### Repeated notification-host output: redundant traversal, mostly incremental skips

The app project lists all four hosts as `ProjectReference` items with `ReferenceOutputAssembly="false"`.
It also contains these explicit targets:

- `RestoreNotificationHost`, before `Restore`, restores the four hosts independently.
- `BuildNotificationHost`, before `PrepareForBuild`, builds them independently in Debug.
- `PublishNotificationHost`, before `PrepareForBuild`, publishes them in Release.

The normal project-reference graph already visits the hosts.
The explicit Debug targets add another traversal and revisit their common dependencies.
The explicit build also adds the global property `Restore=false`.
That property is not the same mechanism as the CLI's `--no-restore` option.

Two consecutive restore commands reproduced the repeated host messages without source edits:

```powershell
dotnet restore src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -v:normal
```

Detailed restore diagnostics reported unchanged inputs, unchanged assets files, and `No-Op restore` for the hosts.
The repeated `Restored ...` lines therefore do not prove changed solution files or rewritten restore assets.
Normal build diagnostics also showed skipped host `CoreCompile` targets.

There is measurable redundant work, but the hosts did not undergo full C# recompilation on every visit.
The second unchanged build spent about 2.1 seconds in `BuildNotificationHost`.
Removing that duplicate traversal alone cannot explain or eliminate the full build delay.

### Main build delay: XAML intermediate compilation

Two consecutive Debug x64 builds passed with zero warnings and errors:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false
```

The first took 62.71 seconds. The second took 63.15 seconds.
Source files did not change between these commands.

The second build's diagnostic log identified this exact invalidation:

```text
Building target "XamlPreCompile" completely.
Input file "...\App.g.cs" is newer than output file "...\Wino.Mail.WinUI.pdb".
```

During XAML pass 1, `App.g.cs` was an empty file with a fresh timestamp.
After pass 2, the file's original content and timestamp returned.
A subsequent plain WinApp build preserved its final hash and timestamp too.
The final `CoreCompile` target skipped compilation because its outputs were current.

The repeated work occurs in intermediate `XamlPreCompile`, not a full final C# rebuild of every project.
The SDK target includes `@(Compile)` in its timestamp inputs, which includes the temporary generated file.
The second build spent about 41.4 seconds in `MarkupCompilePass2` across three XAML projects.
This work accounts for much more delay than the duplicate notification-host target.

This finding explains the observed delay without a solution-file edit.
It does not justify skipping XAML checks after a real source, reference, or resource change.

### Plain launch commands: working directory and packaged run support

At the repository root, `winapp run .` finds `WinoMail.slnx` and several runnable projects.
It requires `--project Wino.Mail.WinUI` to select the app.

From `src/Wino.Mail.WinUI`, this plain project-directory command passed after Debug processes released the layout:

```powershell
winapp run . --detach --json
```

Its build took 69.20 seconds and passed with zero warnings and errors.
It deployed the existing Debug identity and returned PID `46424`.

The project does not currently provide packaged `dotnet run` integration.
MSBuild reports `RunCommand` as the app executable and `_WinAppRunSupportActive` as empty.
The launch profiles contain `MsixPackage` and `Project`, rather than a CLI deployment bridge.
The investigation did not run that executable directly.

Microsoft documents packaged run support through `Microsoft.Windows.SDK.BuildTools.WinApp`.
Its project integration currently invokes folder mode, which conflicts with this repository's project-mode rule.
A project-mode bridge requires a deliberate design rather than adding that package without checking its behavior.

### Sandbox failure: still independent of local package locks

After local launch succeeded and no mail hosts remained, guest diagnostics still failed:

```powershell
winapp target exec sandbox --json -- powershell.exe -NoProfile -Command "Write-Output 'Guest diagnostics ready'"
```

WinApp returned `sandbox_start_failed`, `wsbVerb: share`, and `hresult: 0x80070005`.
This command does not access Wino's package layout.
The local host-file lock therefore does not explain the Sandbox bootstrap failure.
Guest-agent setup and folder-sharing access require their own investigation.

## Local runtime evidence

Current Debug project deployment returned PID `46424` and HWND `16062288`.
WinApp UI commands found `SelectionModeToggle`, invoked it, and observed `ToggleState: On`.
A second invoke restored `Off`, followed by a passing `wait-for` assertion.
The app screenshot showed Dark theme with the main mail UI visible.
These checks establish launch and a reversible interaction, not full mail functionality.

Evidence resides in `artifacts/debug-deployment-investigation-20261001/`:

- `build-evidence.txt`: diagnostic invalidation, final compilation skip, and timings.
- `restore-evidence.txt`: unchanged restore-input and assets-file excerpts.
- `plain-project-run.log`: successful plain project-directory launch.
- `app-generated-before.json` and `app-generated-after.json`: final generated-file hashes and timestamps.
- `local-smoke.png`: app-window screenshot after restoring the selection toggle.

## Implementation plan

1. Add shared deployment preparation for the main app and its four notification hosts.
   Apply package identity checks before process shutdown.
   Scope shutdown to the checked-in Debug registration and report unresolved process ownership.
   Use the same preparation in `wino.ps1`, UI runners, and the packaged run bridge.
2. Remove redundant Debug restore/build traversal where project references already supply the required payload.
   Retain the separate Release publish requirement.
   Check clean restore, missing host output, host-source edits, and unchanged repeated builds.
3. Address the temporary XAML placeholder invalidation through a narrowly scoped project target or an upstream SDK correction.
   Prototype the target before adopting it.
   Check C# edits, XAML edits, resources, reference changes, and the final generated files.
   Keep `-NoBuild` explicit until incremental correctness is established.
4. Add a Debug-only `dotnet run` bridge to WinApp project mode.
   Preserve the checked-in identity, launch arguments, and data.
   Prevent recursive builds and retain compile-only Release behavior.
5. Document root-level project selection and project-directory `winapp run .`.
   Check both entry points and `wino.ps1 run app` against the current Debug app.
6. Continue Sandbox bootstrap diagnosis independently.
   Local deployment remains the default.

## Implemented changes

The app now uses its four project references for notification-host restore and Debug build.
The duplicate `RestoreNotificationHost` and `BuildNotificationHost` targets are removed.
The Release publish target remains in place.

`Wino.Debug.targets` excludes only the empty generated `App.g.cs` placeholder from `XamlPreCompile` inputs in Debug builds.
It restores the original compile item before pass two or final compilation.
It also removes the final compiler's PDB from the precompile output list.
The SDK precompile task uses `DebugType=none` and does not produce that PDB.
The final compiler's original symbol items are restored before pass two and final compilation.
Nonempty generated code, real source, XAML, resources, and references retain their normal invalidation rules.
Design-time and Release compilation use the original inputs.

`Prepare-WinoDebugDeployment` verifies package identity and the checkout's Debug installation path before shutdown.
It collects and verifies process paths before stopping any process.
It uses CIM command lines when the executable path is unavailable.
The harness, UI runner, regression runner, and `dotnet run` bridge share this preparation.

`dotnet run` now activates the package through WinApp project mode after the normal build.
It passes `--no-build` to WinApp to avoid recursion and rejects Release runtime requests.
The misleading Unpackaged launch profile is replaced with a WinApp CLI profile.
Project references receive the app's evaluated platform and runtime.
Without explicit global flags, plain `dotnet run` previously built WinUI references as AnyCPU.
That produced a Win2D architecture warning and selected different outputs from the harness.
The references now use the app's x64 and `win-x64` defaults for project-only builds.
The controls project declares the supported runtime identifiers so plain restore supplies its native assets.

`wino.ps1` remains useful for preparation, tests, affected analysis, and formatting.
It contained no file-change cache to remove.
Plain WinApp remains available after explicit local preparation; it cannot invoke that preparation before `--no-build` deployment.
The harness now restores the caller's working directory on success and failure.

Sandbox bootstrap still requires separate diagnosis of the existing `wsb` access failure.
These local changes do not alter Sandbox setup or stop the running Sandbox.

## Implementation verification

These results record the original verification. The added deployment, run bridge, and incremental-build test scripts were later removed at the user's request.

The script checks passed: 21 harness checks, 10 identity checks, four process preparation checks,
four run bridge checks, and nine regression helper checks.
The bridge checks include native `pwsh -File` invocation, argument boundaries, and failure propagation.
PowerShell parsing, documentation links, and `git diff --check` also passed.

The real Debug integration checks passed with zero build warnings and errors:

- An unchanged build retained both intermediate and final assembly timestamps.
  It took 51.73 seconds, compared with the original builds at roughly 63 seconds.
- A new C# type reached the final assembly and changed its Debug symbols.
  Removing that source also removed the type from the assembly.
- A changed XAML input invalidated precompilation.
- Removing a notification-host executable caused the project graph to rebuild and copy it into the app output.

WinUI still runs its markup passes, so unchanged builds remain costly.
Project resolution, changed build properties, and package materialization can add time to a CLI launch.
No automatic source cache or implicit `--no-build` shortcut was added.

Local launch checks succeeded:

- Plain `dotnet run` from the app directory: PID `44144`, with no AnyCPU warning.
- Root `winapp run . --project Wino.Mail.WinUI --no-build --detach --json`: PID `59044` after preparation.
- App-directory `winapp run . --detach --json`: PID `116040` after preparation.
- The UI runner built and deployed current source, then passed Settings navigation and account-flyout open/dismiss checks.
  After the final target update, the integration suite rebuilt the app and the same UI scenarios passed again.

Final Debug UI evidence uses PID `49332` and HWND `988064`.
`SelectionModeToggle` changed from Off to On and back to Off, with passing property assertions.
The final screenshot shows the Alice Lab Inbox with Dark appearance.
The theme preference remains Use system setting.

The x64 Release build passed with zero warnings and errors in 3 minutes 18 seconds.
It retained the four native notification-host publish steps.
The SDK also emitted unsigned MSIX and symbol outputs despite `GenerateAppxPackageOnBuild=false`.
No Release package was deployed, registered, launched, or UI-tested.

Final evidence:

- `artifacts/incremental-checks/20261001-030100/`: build logs and timings for the input checks.
- `artifacts/ui-tests/20261001-030837/`: final smoke results and screenshots.
- `artifacts/debug-deployment-investigation-20261001/dotnet-run-fixed.log`: plain run activation.
- `artifacts/debug-deployment-investigation-20261001/plain-winapp-final.log`: project-directory WinApp launch.
- `artifacts/debug-deployment-investigation-20261001/toggle-on-final.json` and `toggle-off-final.json`: property assertions.
- `artifacts/debug-deployment-investigation-20261001/local-final.png`: final Debug UI.
- `artifacts/debug-deployment-investigation-20261001/release-compile.log`: Release validation.

## References

- [App build targets](../../src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj)
- [Development harness](../../scripts/wino.ps1)
- [Package preflight](../../scripts/Wino.Debug.ps1)
- [Notification host runtime](../../src/Wino.NotificationHost/Program.cs)
- [Notification host client](../../src/Wino.Mail.WinUI/Services/NotificationHostClient.cs)
- [WinApp 0.7.0 recipe materialization source](https://github.com/microsoft/winappCli/blob/v0.7.0/src/winapp-CLI/WinApp.Cli/Services/MsixService.Identity.cs)
- [Packaged dotnet run support](https://github.com/microsoft/winappCli/blob/main/docs/dotnet-run-support.md)
- [Sandbox execution](https://github.com/microsoft/winappCli/blob/main/docs/sandbox-execution.md)
