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
