# Development commands

Use plain `dotnet` and WinApp CLI commands from the repository root.
The app always runs with MSIX package identity.

## Requirements

- Windows with Developer Mode enabled
- The .NET SDK selected by `global.json`
- WinApp CLI 0.7 or later on `PATH`
- VS Code with Microsoft's C# extension for F5 debugging
- PowerShell 7.2 or later (`pwsh`) for the F5 helper

NuGet restore supplies the Windows App SDK. The app targets .NET 10 and defaults to x64 for development.
Microsoft documents [WinApp project mode](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#project-mode-net-sdk-projects) and [VS Code C# debugging](https://code.visualstudio.com/docs/csharp/debugger-settings).

## VS Code F5

1. Open the repository folder in VS Code.
2. Select **Debug Wino Mail (packaged, x64)** in Run and Debug.
3. Press **F5**.

The launch configuration runs the **Run Wino Mail (packaged, x64)** task.
That task invokes [start-wino.ps1](../../scripts/development/start-wino.ps1), the single approved development helper.
The helper checks input contents, tool versions, and output files against the last successful run.
Changed inputs or missing output trigger `winapp run` with Debug, x64, and `--detach`.
Unchanged inputs and output use `winapp run --no-build --detach`.
WinApp registers the MSIX development layout and activates the application.
VS Code then attaches the C# debugger to `Wino.Mail.WinUI`.

The configuration supplies the build output as a symbol search path because the deployed layout can omit PDB files.
Attach occurs after activation, so startup code can run before the debugger connects.
There are no custom debug build targets.

**Ctrl+Shift+B** runs the separate **Build Wino Mail (Debug, x64)** task without deployment.
The helper hashes files under `src`, `controls`, `icons`, and `.config`, plus root build configuration and the user NuGet configuration.
It excludes generated output directories such as `bin`, `obj`, `AppPackages`, and `artifacts`.
File additions, deletions, and content changes invalidate saved state, including changes that preserve timestamps.
It checks output file names, sizes, and timestamps, including dependencies and the deployed layout.
State is stored in the ignored `artifacts/f5/last-success.json` file.
The first run builds. Failed runs and edits during a run force a build on the next F5.
Concurrent helper runs are rejected.

The helper checks the installed package identity before stopping processes from that development package.
It closes those processes before deployment, including the tray process. Application data is preserved.

After changing external build inputs or environment settings, force a build:

```powershell
pwsh -NoProfile -File scripts/development/start-wino.ps1 -ForceBuild
```

The cache covers the repository inputs listed above. It does not track arbitrary external imports or changes inside installed tool packages.

## Terminal commands

Build without deployment:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Build, deploy, and activate the checked-in package:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --arch x64 --detach
```

Deploy an existing build without compilation:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --arch x64 --no-build --detach
```

The manifest supplies package name `58272BurakKSE.WinoMailPreview` and publisher `CN=51FBDAF3-E212-4149-89A2-A2636B3BC911`.
WinApp activates the registered package. It does not launch an unpackaged executable.
A development layout provides MSIX identity without creating an installer on every run.

Build commands restore packages by default. MSBuild decides which outputs need rebuilding.
WinUI can repeat XAML compilation without source changes. The F5 helper skips the entire build when its saved state still matches.

### Why an unchanged F5 build can be slow

Diagnostic logs captured on 2026-10-01 used .NET SDK 10.0.301, WinApp CLI 0.7.0, and WinUI package 2.3.9.
The second unchanged build took 60.7 seconds. All 20 `CoreCompile` targets were skipped, and final DLL timestamps stayed unchanged.
The build still spent 39.1 seconds in `MarkupCompilePass2` and 6.3 seconds in `XamlPreCompile` across the WinUI projects.

MSBuild reported that generated `App.g.cs` was newer than `Wino.Mail.WinUI.pdb`.
WinUI temporarily empties generated pass-2 source files during pass 1, then restores their contents.
The SDK's `XamlPreCompile` target lists the final PDB as an output, although its C# invocation uses `DebugType="none"`.
That target therefore repeats intermediate compilation while the final app assembly can remain current.
See the [Microsoft MSBuild target](https://github.com/dotnet/msbuild/blob/main/src/Tasks/Microsoft.CSharp.CurrentVersion.targets) and [WinUI compiler implementation](https://github.com/microsoft/microsoft-ui-xaml/blob/main/src/XamlCompiler/BuildTasks/CompileXamlInternal.cs).

WinApp also restores the solution without `Platform=x64`, then builds the app with that property.
The second restore rewrites dependency assets and causes dependency JSON files to regenerate.
Passing `-p Platform=x64` to WinApp 0.7.0 did not change its solution restore command.
A build with `--no-restore` still took 54.4 seconds and repeated the XAML work.

The F5 helper avoids these steps when its inputs and output are unchanged.
Changed inputs still use the normal SDK build. The helper does not replace any SDK compilation targets.

## Package troubleshooting

Before deployment, inspect the installed package:

```powershell
Get-AppxPackage -Name 58272BurakKSE.WinoMailPreview | Select-Object Name, Publisher, IsDevelopmentMode, InstallLocation
```

The publisher must match the source manifest. An existing installation must permit development deployment.
If a signed Store installation owns this identity, use a development machine with the same checked-in identity.
Preserve the existing installation and its data.

If deployment reports a locked file, exit Wino and its notification hosts before retrying.
The [historical deployment investigation](debug-deployment-investigation.md) describes notification-host file locks.
Do not use `--clean`, unregister the package, or change its identity to solve a launch error.

For startup diagnostics without the VS Code debugger:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --arch x64 --debug-output
```

Only one debugger can attach to a process. Exit this diagnostic session before F5 debugging.

## Windows Sandbox with WinApp CLI

Use WinApp CLI 0.7.1 or later and an enabled Windows Sandbox on Windows 11 24H2 or newer.
Builds run on the host; package registration and activation run in the guest.
See Microsoft's [Sandbox prerequisites and execution guide](https://github.com/microsoft/WinAppCli/blob/main/docs/sandbox-execution.md).

In VS Code, select **Run Wino Mail (Sandbox, Debug x64)** in Run and Debug and press **F5**.
The same named task is available through **Tasks: Run Task**. Both invoke the existing helper:

```powershell
pwsh -NoProfile -File scripts/development/start-wino.ps1 -Target sandbox
```

The helper checks the guest package identity and development status, closes its app and tray processes,
then uses `winapp run -c Debug --arch x64 --on sandbox --detach --json`.
It shares the host build cache with local F5; add `-ForceBuild` to bypass that cache.
Guest preflight failure stops deployment. The helper preserves application data and leaves Sandbox running.
This F5 option uses VS Code's built-in terminal launcher to run the Debug build; it does not attach a C# debugger.
For managed debugging on the host, select **Debug Wino Mail (packaged, x64)**.

Before deployment, inspect the guest installation:

```powershell
winapp target exec sandbox --json -- powershell.exe -NoProfile -Command "Get-AppxPackage -Name 58272BurakKSE.WinoMailPreview | Select-Object Name,Publisher,IsDevelopmentMode,InstallLocation | ConvertTo-Json"
```

If installed, its publisher must match `CN=51FBDAF3-E212-4149-89A2-A2636B3BC911` and `IsDevelopmentMode` must be true.
Stop on a mismatch or a signed non-development installation. Close the guest Debug app and its tray process before redeployment.

Build, deploy, and activate the checked-in package in Sandbox:

```powershell
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --arch x64 --on sandbox --detach --json
```

After a successful Debug x64 build, add `--no-build` to deploy that output.
The run result must identify the Sandbox target and include the guest process ID.
Preserve guest application data: do not use `--clean`, unregister the package, or stop an existing Sandbox to retry.
Use manual interaction for UI verification; successful activation alone does not verify mail behavior.

## Unit tests and manual lab checks

Run the affected unit-test project directly:

```powershell
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~RelevantTestClass"
dotnet test tests/Wino.Mail.ViewModels.Tests/Wino.Mail.ViewModels.Tests.csproj -c Debug -p:Platform=x64
dotnet test tests/Wino.Mail.Controls.Tests/Wino.Mail.Controls.Tests.csproj -c Debug -p:Platform=x64
```

Use the [local Docker lab](../../tools/local-lab/README.md) for manual mail, calendar, and contact checks.
The lab provisions servers and generates account data. It does not launch or automate Wino.
Agent-driven UI suites, recording scripts, automation-ID audits, and Sandbox test orchestration are retired.
Report manual checks separately from compilation and unit-test results.

## Release and maintenance

Compile Release without deployment:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
```

Use [release packaging](../releases.md) to create distribution artifacts.
Its scripts, profiles, and payload export targets live under `scripts/release`.
Shared release analyzer and Native AOT packaging rules remain in `Directory.Build.targets`.

```powershell
pwsh -File scripts/release/build-releases.ps1
pwsh -File scripts/maintenance/format-xaml.ps1 -Changed
pwsh -File scripts/maintenance/format-xaml.ps1 -Changed -Check
```

The [script index](../../scripts/README.md) describes every retained script category.

## Native macOS F5

On Apple Silicon, open [WinoMail.MacOS.code-workspace](../../WinoMail.MacOS.code-workspace), select **Debug Wino Mail (macOS, arm64)**, and press **F5**. The workspace selects `WinoMail.MacOS.slnx`; the repository folder's Windows solution default stays unchanged.

Install the SDK selected by `global.json`, the matching macOS workload and Xcode, and Microsoft's C# extension. Let the extension finish installing its debugger. VS Code must run on the Mac, either locally or with an extension host on the Mac. Native AppKit windows appear on the Mac desktop.

The build task first runs [setup-macos-debugger.sh](../../scripts/maintenance/setup-macos-debugger.sh), then uses plain `dotnet build`:

```sh
dotnet build src/Wino.Mail.MacOS/Wino.Mail.MacOS.csproj -c Debug -r osx-arm64 -p:WinoTargetRuntimeIdentifier=osx-arm64 -p:WinoTargetPlatform=MacOS -p:EnableWindowsTargeting=true
```

The C# debugger launches the bundle's native executable under `Wino Mail.app/Contents/MacOS/Wino.Mail.MacOS`. This production Debug build uses CoreCLR. Both the application environment and `pipeTransport.pipeEnv` set `TMPDIR` to `~/Library/Containers/com.winomail.desktop/Data/tmp`. Setting only `launch.env` does not change the debugger's environment. The supported [C# pipe transport](https://github.com/dotnet/vscode-csharp/blob/main/docs/debugger/Attaching-to-remote-processes.md) uses `/bin/zsh -c` to start the prepared debugger on the same machine.

### Debugger compatibility on macOS 26

The C# extension tested here is `2.160.4`, with debugger `18.12.10903.2`. Its bundled diagnostic shim `9.0.12.27201` did not establish the sandboxed launch handshake: the app ran, while source breakpoints remained unprocessed. Microsoft tracks the macOS 26 semaphore restriction in [runtime issue 116545](https://github.com/dotnet/runtime/issues/116545); current shims use startup FIFOs.

The maintenance task discovers the installed C# extension through `code --locate-extension ms-dotnettools.csharp`, copies its matching debugger into `~/.local/share/wino-vsdbg`, and replaces only the copied `libdbgshim.dylib` with Microsoft's [Microsoft.Diagnostics.DbgShim.osx-arm64 10.0.745401](https://www.nuget.org/packages/Microsoft.Diagnostics.DbgShim.osx-arm64/10.0.745401) package (native version `10.0.14.45401`). It checks the package SHA-256 `adcac4c69e2e5a29f6dc9df8a8ba589011a183bf505882cc37d137f63545375e` and installed shim checksum. This is a versioned compatibility workaround, not a debugger distribution supplied by the C# extension. No binaries are checked into the repository.

The installed extension remains untouched. Setup repeats after an extension/debugger version change or a missing/corrupt prepared shim; unchanged tooling requires no download. Do not substitute an independently downloaded `vsdbg`: the tested `getvsdbgsh -v latest` returned `18.10.10709.3`, which rejected the `18.12` adapter's source breakpoint requests with `Incorrect breakpoint request format.` Extension updates require another breakpoint check; the shim pin is not a guarantee of compatibility with future adapters.

Run setup manually to repair tooling:

```sh
bash scripts/maintenance/setup-macos-debugger.sh
```

The first setup needs access to nuget.org. If the VS Code CLI is outside `PATH` and the standard `/Applications/Visual Studio Code.app` location, set `WINO_VSCODE_CLI` to its executable path. Setup preserves replaced debugger directories as `~/.local/share/wino-vsdbg.previous.<timestamp>`. To roll back, stop debugging, move the current prepared directory aside and restore the previous one; automatic F5 setup will prepare the currently configured shim again, so a deliberate rollback also requires reverting the setup pin/configuration. The app's sandbox, signing entitlements, identity, account database and Keychain references are unchanged.

### Mac F5 acceptance

On 2026-10-07, the production arm64 app hit a source breakpoint at `Program.cs:9`, stepped to line 10, then hit `AppDelegate.StartAsync` at `AppDelegate.cs:38` with locals and managed stack frames visible. Continuing opened the saved account's native shell and Inbox. A final run through the Mac workspace repeated the `Program.cs:9` breakpoint, stepping and shell continuation with exact source checks and automatic maintenance setup. Evidence is recorded locally in `artifacts/appkit-macos-f5-acceptance.txt`, with a breakpoint screenshot inline in the Codex chat. These checks establish debugger startup and continuation; fresh authentication, Remote SSH debugging, full UI parity and manual lab acceptance remain separate work.
