# Development commands and runtime verification

Read the relevant section for builds, diagnostics, deployment, or UI verification.

The root `AGENTS.md` defines task scope and package boundaries.
Its package rules also apply to the personal Wino runtime skill.

## Build and test

Use the repository harness for the normal development loop:

```powershell
.\scripts\wino.ps1 affected
.\scripts\wino.ps1 affected -Path src/Wino.Services/MailService.cs
.\scripts\wino.ps1 build app
.\scripts\wino.ps1 build app -Configuration Release
.\scripts\wino.ps1 test core -Filter "FullyQualifiedName~RelevantTestClass"
```

Use `-Path` for task-specific affected analysis in a dirty worktree.
The Release command compiles without deployment or launch. Runtime commands accept only Debug.
For harness argument and failure checks, run `pwsh -NoProfile -File tests/scripts/Wino-Harness.Tests.ps1`.

Use the expanded commands below for diagnostics or when the harness does not cover a required option.

Restore after package, project, framework, or runtime inputs change, or when required restore assets are missing:

```powershell
dotnet restore src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --configfile nuget.config -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Use this command for a compile-only WinUI check. It does not deploy the application registered with Windows:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false
```

If a task requires Release or Native AOT validation, build the app without launching it. Never deploy or test the Release package:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false
```

Run the narrowest affected tests. After a successful build of the same test project, use `--no-build --no-restore` for repeated runs:

```powershell
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug /p:Platform=x64 --no-restore
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug /p:Platform=x64 --no-build --no-restore --filter "FullyQualifiedName~RelevantTestClass"
```

Use changed files and CodeGraph to select tests before falling back to the complete test project:

```powershell
git diff --name-only --diff-filter=ACMR | codegraph affected --stdin --quiet
```

If a WinUI build reports only `XamlCompiler.exe exited with code 1`, rerun with diagnostics and inspect the first real `WMC`, `WMC1121`, or binding error:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug --no-restore /p:Platform=x64 /p:RuntimeIdentifier=win-x64 /p:GenerateAppxPackageOnBuild=false /p:AppxPackageSigningEnabled=false "/flp:logfile=winui-build.log;verbosity=diagnostic" /bl:winui-build.binlog
```

Do not create diagnostic logs or binlogs for successful routine builds.

## Installed Debug application

Use WinApp CLI 0.6 or later in project mode for the normal development cycle. Project mode accepts the `.csproj` as input. It builds the project and activates the package with its existing manifest identity.

Before runtime work, run the shared read-only preflight:

```powershell
.\scripts\wino.ps1 doctor app
```

The JSON report includes CLI version, identity, publisher, installation path, signature kind, and development-mode status.
`run`, `debug`, `ui`, and scripted `audit` use this guard before deployment or process shutdown.
Doctor reports readiness without changing packages. A blocked runtime command returns a failure.

For manual diagnostics, inspect the manifest and installed package:

```powershell
$manifest = [xml](Get-Content 'src/Wino.Mail.WinUI/Package.appxmanifest')
$identity = $manifest.Package.Identity
Get-AppxPackage -Name $identity.Name | Select-Object Name, Publisher, PackageFamilyName, InstallLocation, IsDevelopmentMode, SignatureKind
```

Matching identity is necessary but not sufficient. The installed package must also permit development-mode deployment.
WinApp refuses to replace a signed non-development installation with a development registration.
If the publisher differs or a signed installation owns the identity, stop before building or stopping the app.

The local check on 2026-09-15 found version 2.1.0.0 under `WindowsApps`, with `SignatureKind=Developer` and `IsDevelopmentMode=false`.
Thus the current blocker is an installed signed package, not specifically a Store signature or an untrusted certificate.
The recorded project-mode attempt returned `InstalledPackageConflict` behavior without launching the application.

### Coexistence with Store testing

Use a dedicated Windows development VM for Debug deployment while retaining the existing installed app and its data on the host.
Use the same checked-in manifest in the VM. Run `doctor app`, then the standard project-mode command.
Configure test accounts explicitly in that environment. Do not copy production app storage as an automatic setup step.

A separate Windows user can still encounter packages staged on the same machine, so it is not a guaranteed fix.
Replacing the current installation requires a separate migration decision and a verified data backup/restore plan.
The harness does not uninstall, unregister, create a VM, or change package identity automatically.
Live verification remains pending until a suitable development environment is available.

WinApp's package and data behavior is documented in its [command reference](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md).

Build, update the existing Debug registration, launch, and return the PID for UI automation:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -r win-x64 --no-restore -p Platform=x64 -p GenerateAppxPackageOnBuild=false -p AppxPackageSigningEnabled=false --detach --json
```

When the current Debug output is already built, skip compilation for the fastest relaunch:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -r win-x64 --no-build --no-restore -p Platform=x64 -p GenerateAppxPackageOnBuild=false -p AppxPackageSigningEnabled=false --detach --json
```

For launch or crash diagnosis, omit `--detach --json` and use `--debug-output`. This option keeps WinApp CLI attached. It captures first-chance exceptions and analyzes WinUI stowed exceptions after a crash. Do not attach another debugger at the same time:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -r win-x64 --no-restore -p Platform=x64 -p GenerateAppxPackageOnBuild=false -p AppxPackageSigningEnabled=false --debug-output
```

Obey these package rules:

- Use the checked-in manifest and the existing package family.
- Preserve application data between deployments.
- Never use folder mode, `winapp init`, or `winapp create-debug-identity`.
- Never use `--clean` or `--unregister-on-exit`.
- Never rewrite the manifest or create a second package identity.
- Never run `Wino.Mail.WinUI.exe` directly.
- Never use `winapp run` with Release.

## WinApp UI verification

WinApp CLI is the only supported way to run the application and capture visual evidence. Never use desktop automation, computer use, screen capture of the whole desktop, or any tool that drives the mouse and keyboard against the running app. Those tools front the wrong window, capture the wrong monitor, and produce evidence that cannot be trusted. Use `winapp run` to launch and `winapp ui` to inspect, interact, and screenshot. This applies to the playground and every other packaged project in this repository, not only to `Wino.Mail.WinUI`.

After the current x64 Debug build has been deployed and started, use WinApp CLI directly against the running process:

```powershell
winapp ui list-windows -a Wino.Mail.WinUI --json
winapp ui status -a Wino.Mail.WinUI --json
winapp ui inspect -a Wino.Mail.WinUI --interactive --depth 8 --json
```

If more than one window matches, take the stable HWND from `list-windows` and use `-w <HWND>` for every subsequent command.

Exercise the changed behavior with `winapp ui invoke`, `click`, `set-value`, `focus`, or `scroll-into-view`. Assert the result with `wait-for`, `get-value`, or `get-property`. Capture visual evidence only when layout, theme, clipping, overlap, popup, or window behavior matters:

```powershell
winapp ui wait-for "AutomationIdOrName" -a Wino.Mail.WinUI --timeout 5000
winapp ui screenshot -a Wino.Mail.WinUI --json -o artifacts\wino-ui-current.png
```

For timing-dependent or transient visual behavior, record a short bounded clip with agent-readable frames instead of taking many screenshots:

```powershell
winapp ui record -w <HWND> --duration-sec 10 --frames --fps 5 --max-edge 1280 --json -o artifacts\wino-ui-current.mp4
```

Prefer stable `AutomationProperties.AutomationId` values over localized labels. For changed XAML, run the existing static audit before UI verification:

```powershell
.\scripts\audit-xaml-automationids.ps1
```

A visible window, screenshot, or recording is not a passing interaction test. Report the action, assertion, process or HWND, and verified theme. Before testing, verify that project mode deployed the current source. Otherwise, report that UI verification is pending.

## Verification matrix

- Domain or service logic: build the affected project and run the directly affected unit tests.
- ViewModel or messenger changes: run affected unit tests, then verify the UI-bound state through the installed Debug app when behavior changed.
- XAML, code-behind, navigation, activation, windowing, or controls: redeploy the existing Debug package with WinApp CLI project mode. Use Visual Studio only when interactive debugging is required. Then run the automation-ID audit and exercise the affected flow with WinApp CLI.
- Reusable controls: follow `controls/AGENTS.md`, update the playground, and verify the relevant control states and themes.
- Localization: change only `en_US/resources.json`, build the generator output, and leave other locale files untouched.
- Package, trimming, or Native AOT work: use an explicit compile-only Release build. Never deploy, launch, or UI-test Release. Use the existing Debug registration for all runtime and UI verification.


