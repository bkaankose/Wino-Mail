# Windows development

This is the authoritative guide for the WinUI 3 application in `src/MailCal.Mail.WinUI` and its solution `MailCal.slnx`.
Use plain `dotnet` and WinApp CLI commands from the repository root. The app always runs with MSIX package identity.

Shared rules for every platform are in [AGENTS.md](../../AGENTS.md) and the [implementation rules](implementation-rules.md).

## Requirements

- Windows with Developer Mode enabled
- The .NET SDK selected by `global.json`
- WinApp CLI 0.7 or later on `PATH`
- VS Code with Microsoft's C# extension for F5 debugging
- PowerShell 7.2 or later (`pwsh`) for the F5 helper
- For the Release x64 gate (Native AOT): Windows SDK 10.0.26100 and the MSVC C++ build tools. Without the SDK, CsWinRT fails with `Could not find the Windows SDK in the registry`; without MSVC, the AOT compiler reports `Platform linker not found`.

  ```powershell
  winget install --id Microsoft.WindowsSDK.10.0.26100 --source winget
  winget install --id Microsoft.VisualStudio.2022.BuildTools --source winget --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
  ```

  Open a new terminal after installing. The AOT linker setup calls `vswhere.exe` through `PATH`, which only new sessions pick up.

NuGet restore supplies the Windows App SDK. The app targets .NET 10 and defaults to x64 for development.
Microsoft documents [WinApp project mode](https://github.com/microsoft/WinAppCli/blob/main/docs/usage.md#project-mode-net-sdk-projects) and [VS Code C# debugging](https://code.visualstudio.com/docs/csharp/debugger-settings).

## Native AOT and build safety — critical

Native AOT compatibility is a release-blocking correctness requirement. A binding that works in Debug can fail when users open a page in Release. Prevent these failures during implementation; Mail-Cal has no crash telemetry, so a Release-only failure reaches you only through a user report.

- Prefer compiled `x:Bind` for application properties and commands, including `CopyClipboardCommand`. Do not introduce runtime `{Binding}` paths where compiled bindings suffice.
- When runtime bindings are necessary, audit every custom CLR object traversed by the path. For `ViewModel.Property`, both the page's `ViewModel` getter and the ViewModel's property need generated binding support. Use narrowly scoped `WinRT.GeneratedBindableCustomProperty` annotations on partial types, guarded by `WINRT_EXPOSED` in shared Windows/portable code. Framework and dependency-property bindings do not require blanket annotations.
- Include bindings inside templates and bindings created with `SetBinding` in the audit. For new or changed generated binding support, inspect the generated accessors as well as the build result. Annotating only the first object in a multi-part path is insufficient.
- Give WinRT-facing collections explicit concrete backing types. Use an array or `List<T>`, or an explicit array cast such as `(Option[])[...]`; never leave collection expressions targeting read-only interfaces with an implicit compiler-generated backing type. See the [implementation rules](implementation-rules.md#core-implementation-rules).
- Give every value that reaches an items source a concrete static type: `ItemsSource`, `MenuItemsSource`, `FooterMenuItemsSource`, `TabItemsSource`, and `CollectionViewSource.Source`, whether set by `x:Bind`, a function binding, or code. CsWinRT generates WinRT vector support only for concrete collection types it sees crossing into WinRT. A property typed `IReadOnlyList<T>`, `IEnumerable<T>`, `object`, or a generic `T[]` hides the runtime collection, and the control throws `Argument 'source' is not a supported vector` in Release while Debug works. The `MCAOT001` analyzer in `MailCal.SourceGenerators` fails the build at the binding and names the XAML line. Fix the source type; do not suppress the diagnostic.
  - Expose an array, `List<T>`, `ObservableCollection<T>`, or a concrete `[GeneratedWinRTExposedType]` subclass such as `AliasManagementItemList`. When implementations of a shared interface return different collection types, give the interface member one concrete type.
  - A public collection property on a XAML page, dialog, or control registers its element type in the generated XAML metadata, which fails to compile for records with `init` setters (`CS8852`). Make such code-behind properties `internal`; `x:Bind` still reaches them.
  - A polymorphic source that cannot have one concrete type can root each runtime type with `[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(List<Item>))]`, but prefer a concrete static type, which the analyzer can verify.
- Release builds run every trim, AOT, single-file, and CsWinRT analyzer with warnings as errors: the Roslyn analyzers during compilation and ILC during the Native AOT link. CsWinRT runs at `CsWinRTAotWarningLevel` 3, its strictest level. A cast or `as` to a WinRT runtime class (`FrameworkElement`, `Button`, `DataTemplate`, …) or an unboxing of a WinRT enum (`Visibility`, `Orientation`) reports `CsWinRT1034`/`CsWinRT1035`. Fix it with `[WinRT.DynamicWindowsRuntimeCast(typeof(T))]` on the containing method, accessor, constructor, or field. Do not suppress the warning. CsWinRT's code fixer applies the attribute in bulk: set `Configuration=Release`, `Platform=x64`, and `RuntimeIdentifier=win-x64` in the environment, then run `dotnet format analyzers <project> --diagnostics CsWinRT1034 CsWinRT1035 --severity info`.
- Do not add `IL*` or `WMC1510` codes to `NoWarn`. ILC reports every assembly's warnings in detail. The only exception is the upstream allowlist in `build/MailCal.Windows.targets` (`MailCalCollapseUpstreamTrimWarnings`): Ical.Net collapses to a single summary warning that is ignored, and the WinUI project roots the whole assembly. A package that declares itself trimmable always reports in detail. Remove an unreachable unsafe path with a trimmer substitution (`ILLink.AotSubstitutions.xml`); the MimeKit default S/MIME context is removed this way. Fix a new package warning at the call site, by updating the package, or with a trimmer root. Add a package to the allowlist only with a comment explaining why it is safe at runtime.
- DataTemplates in a library's `Themes/Generic.xaml` cannot declare `x:DataType`, because that dictionary has no `x:Class`. Put item templates in a class-backed dictionary such as `Themes/MailCalItemTemplates.xaml`, bind them with `x:Bind`, and merge that dictionary into `Generic.xaml`. Inside a `ControlTemplate`, use `TemplateBinding` instead of `{Binding …, RelativeSource={RelativeSource TemplatedParent}}`.
- Register event handlers in XAML wherever XAML can express them, including `Loaded`, `Unloaded`, and input events. Do not subscribe with `+=`/`-=` in code-behind when a XAML attribute works; reserve code subscriptions for events XAML cannot reach, such as `XamlRoot.Changed`, and prefer a XAML-only design over adding them.
- Changes to production C#, XAML, bindings, WinRT-facing models, dependencies, or build configuration require both the Debug x64 build and the Release x64 gate below. Documentation-only changes do not require builds. Fix introduced errors and related build blockers before handoff; if an unrelated blocker prevents verification, report the failed check and do not claim Release/AOT validation passed.

Run the Release gate without launching or deploying:

```powershell
dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
```

Keep manual lab verification separate: successful compilation and generated accessors do not prove runtime behavior.

## Package and runtime boundaries

- Keep the app packaged as MSIX with the checked-in manifest identity and publisher.
- Use WinApp CLI 0.7+ project mode for development deployment and activation.
- Preserve application data. Do not use `--clean`, unregister packages, or create another development identity.
- Before deployment, compare the installed package identity and publisher with the manifest. Stop on a mismatch or a signed non-development installation.
- Close the Debug application, including its tray process, before deployment if it holds package files open.
- Never launch the packaged executable directly. Release validation compiles and packages without launching.
- Agent-driven UI automation is retired. Do not create or run UI test scripts or require `winapp ui` audits.

## VS Code F5

1. Open the repository folder, or [MailCal.Windows.code-workspace](../../MailCal.Windows.code-workspace), in VS Code. Both select `MailCal.slnx`.
2. Select **Debug Mail-Cal (packaged, x64)** in Run and Debug.
3. Press **F5**.

The launch configuration runs the **Run Mail-Cal (packaged, x64)** task.
That task invokes [start-mailcal.ps1](../../scripts/development/windows/start-mailcal.ps1), the single approved development helper.
The helper checks input contents, tool versions, and output files against the last successful run.
Changed inputs or missing output trigger `winapp run` with Debug, x64, and `--detach`.
Unchanged inputs and output use `winapp run --no-build --detach`.
WinApp registers the MSIX development layout and activates the application.
VS Code then attaches the C# debugger to `MailCal.Mail.WinUI`.

The configuration supplies the build output as a symbol search path because the deployed layout can omit PDB files.
Attach occurs after activation, so startup code can run before the debugger connects.
There are no custom debug build targets.

**Ctrl+Shift+B** runs the separate **Build Mail-Cal (Debug, x64)** task without deployment.
The helper hashes files under `src`, `controls`, `icons`, `build`, and `.config`, plus root build configuration and the user NuGet configuration.
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
pwsh -NoProfile -File scripts/development/windows/start-mailcal.ps1 -ForceBuild
```

The cache covers the repository inputs listed above. It does not track arbitrary external imports or changes inside installed tool packages.

## Terminal commands

Build without deployment:

```powershell
dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

Build, deploy, and activate the checked-in package:

```powershell
winapp run src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj --arch x64 --detach
```

Deploy an existing build without compilation:

```powershell
winapp run src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj --arch x64 --no-build --detach
```

The manifest supplies package name `hairokuma.MailCal` and publisher `CN=hairokuma`.
WinApp activates the registered package. It does not launch an unpackaged executable.
A development layout provides MSIX identity without creating an installer on every run.

Build commands restore packages by default. MSBuild decides which outputs need rebuilding.
WinUI can repeat XAML compilation without source changes. The F5 helper skips the entire build when its saved state still matches.

### Why an unchanged F5 build can be slow

Diagnostic logs captured on 2026-10-01 used .NET SDK 10.0.301, WinApp CLI 0.7.0, and WinUI package 2.3.9.
The second unchanged build took 60.7 seconds. All 20 `CoreCompile` targets were skipped, and final DLL timestamps stayed unchanged.
The build still spent 39.1 seconds in `MarkupCompilePass2` and 6.3 seconds in `XamlPreCompile` across the WinUI projects.

MSBuild reported that generated `App.g.cs` was newer than `MailCal.Mail.WinUI.pdb`.
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
Get-AppxPackage -Name hairokuma.MailCal | Select-Object Name, Publisher, IsDevelopmentMode, InstallLocation
```

The publisher must match the source manifest. An existing installation must permit development deployment.
If a signed Store installation owns this identity, use a development machine with the same checked-in identity.
Preserve the existing installation and its data.

If deployment reports a locked file, exit Mail-Cal and its notification hosts before retrying.
The [historical deployment investigation](debug-deployment-investigation.md) describes notification-host file locks.
Do not use `--clean`, unregister the package, or change its identity to solve a launch error.

One exception: if the F5 helper reports `The development package install location is missing`, the package was registered from a folder that no longer exists (for example a deleted or moved clone). Copy `%LOCALAPPDATA%\Packages\<package family>\LocalState` somewhere safe, run `Remove-AppxPackage -Package <PackageFullName> -PreserveApplicationData`, and start F5 again. The app data stays in place.

For startup diagnostics without the VS Code debugger:

```powershell
winapp run src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj --arch x64 --debug-output
```

Only one debugger can attach to a process. Exit this diagnostic session before F5 debugging.

## Windows Sandbox with WinApp CLI

Use WinApp CLI 0.7.1 or later and an enabled Windows Sandbox on Windows 11 24H2 or newer.
Builds run on the host; package registration and activation run in the guest.
See Microsoft's [Sandbox prerequisites and execution guide](https://github.com/microsoft/WinAppCli/blob/main/docs/sandbox-execution.md).

In VS Code, select **Run Mail-Cal (Sandbox, Debug x64)** in Run and Debug and press **F5**.
The same named task is available through **Tasks: Run Task**. Both invoke the existing helper:

```powershell
pwsh -NoProfile -File scripts/development/windows/start-mailcal.ps1 -Target sandbox
```

The helper checks the guest package identity and development status, closes its app and tray processes,
then uses `winapp run -c Debug --arch x64 --on sandbox --detach --json`.
It shares the host build cache with local F5; add `-ForceBuild` to bypass that cache.
Guest preflight failure stops deployment. The helper preserves application data and leaves Sandbox running.
This F5 option uses VS Code's built-in terminal launcher to run the Debug build; it does not attach a C# debugger.
For managed debugging on the host, select **Debug Mail-Cal (packaged, x64)**.

Before deployment, inspect the guest installation:

```powershell
winapp target exec sandbox --json -- powershell.exe -NoProfile -Command "Get-AppxPackage -Name hairokuma.MailCal | Select-Object Name,Publisher,IsDevelopmentMode,InstallLocation | ConvertTo-Json"
```

If installed, its publisher must match `CN=hairokuma` and `IsDevelopmentMode` must be true.
Stop on a mismatch or a signed non-development installation. Close the guest Debug app and its tray process before redeployment.

Build, deploy, and activate the checked-in package in Sandbox:

```powershell
winapp run src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj --arch x64 --on sandbox --detach --json
```

After a successful Debug x64 build, add `--no-build` to deploy that output.
The run result must identify the Sandbox target and include the guest process ID.
Preserve guest application data: do not use `--clean`, unregister the package, or stop an existing Sandbox to retry.
Use manual interaction for UI verification; successful activation alone does not verify mail behavior.

## XAML formatting

Format changed XAML with the repository-pinned XAML Styler before building, and run the passive check before handoff:

```powershell
pwsh -File scripts/maintenance/windows/format-xaml.ps1 -Changed
pwsh -File scripts/maintenance/windows/format-xaml.ps1 -Changed -Check
```

The editor extension is a convenience. The pinned command-line result is the formatting source of truth.

## Manual lab checks

The repository has no automated test projects. Verify behavior with builds and manual checks in the running application.

The [local Docker lab](../../tools/local-lab/README.md) is the application testing environment. It currently runs on Windows only.
Its entry point is `scripts/lab/windows/local-lab.ps1`. `up` provisions five IMAP/SMTP/DAV servers (QRESYNC, CONDSTORE-only, and UID-only variants) with generated mail and generates account data; `install-app` installs that data into the Debug package and launches Mail-Cal. Nothing automates the UI.
Agent-driven UI suites, recording scripts, automation-ID audits, and Sandbox test orchestration are retired.
Report manual checks separately from compilation results.

## Release and maintenance

Use the [Windows release guide](../releases-windows.md) to create distribution artifacts.
Its scripts, profiles, and payload export targets live under `scripts/release/windows`; beta artwork lives under `scripts/release/Assets/Beta`.
Shared release analyzer and Native AOT rules remain in `Directory.Build.targets`; Windows-only CsWinRT settings and MSIX payload handling live in `build/MailCal.Windows.targets`.

```powershell
pwsh -File scripts/release/windows/build-releases.ps1
```

The [script index](../../scripts/README.md) describes every retained script category.
