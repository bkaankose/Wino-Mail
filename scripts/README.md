# Repository scripts

Scripts are grouped by purpose, then by platform. Run them from any directory; each resolves the repository root from its own location.
Terminal builds and launches use plain `dotnet`, `winapp`, and platform tools. VS Code F5 on Windows uses one PowerShell helper to decide whether WinApp needs to build.
See the [Windows](../docs/harness/development-windows.md) and [macOS](../docs/harness/development-macos.md) development guides.

| Category | Script | Platform | Shell | Purpose |
| --- | --- | --- | --- | --- |
| Development | `development/windows/start-wino.ps1` | Windows | PowerShell 7.2+ | Check build inputs and output, then build if needed and launch the packaged Debug app |
| Release | `release/windows/build-releases.ps1` | Windows | PowerShell 7 | Optionally translate resources, then build and package Store, Beta, and sideload releases |
| Release | `release/windows/publish-releases.ps1` | Windows | PowerShell 7 | Publish Beta and sideload releases to download.winomail.app |
| Release | `release/windows/upload-sentry-symbols.ps1` | Windows | PowerShell 7 | Upload symbols for a selected release |
| Release | `release/macos/build-macos-release.sh` | macOS | Bash | Optionally translate resources, then build the signed, notarized macOS DMG |
| Release | `release/macos/build-macos-appstore.sh` | macOS | Bash | Optionally translate resources, then build, validate, and optionally upload the Mac App Store package |
| Release | `release/whats-new/validate.ps1` | Both | PowerShell 7 | Validate release notes and illustration references |
| Lab | `lab/windows/local-lab.ps1` | Windows | PowerShell 7 | Provision Docker mail/DAV servers and generate test account data |
| Maintenance | `maintenance/windows/format-xaml.ps1` | Windows | PowerShell 7 | Format WinUI XAML or check formatting with the pinned XAML Styler |
| Maintenance | `maintenance/windows/audit-xaml-accessibility.ps1` | Windows | PowerShell | Report XAML controls without accessible labels |
| Maintenance | `maintenance/windows/audit-xaml-icons.ps1` | Windows | PowerShell | Check use of the Wino icon system in WinUI code |
| Maintenance | `maintenance/windows/build-notification-icons.ps1` | Windows | PowerShell | Render toast button icons with theme, contrast, and scale variants; `-AttributionLogos` adds missing app logo target sizes to the app and the beta artwork |
| Maintenance | `maintenance/macos/setup-macos-debugger.sh` | macOS (Apple Silicon) | Bash | Prepare the matching VS Code C# debugger and pinned Microsoft diagnostic shim for sandboxed Mac F5 |
| Localization | `localization/translate_resources.py` | Both | Python 3.10+ | Generate translations from English resources |
| Localization | `localization/validate_resources.py` | Both | Python 3.10+ | Audit or repair translations |

`release/windows/profiles` contains release channel configuration.
`release/windows/Wino.Release.targets` exports the release payload used by the Windows release script.
`release/Assets/Beta` contains the Windows beta artwork.
The root `Directory.Build.targets` retains shared compiler and Native AOT rules; `build/Wino.Windows.targets` holds the Windows-only packaging rules.

Native-only scripts check the host first and stop on the wrong platform.

## Examples

```powershell
pwsh -File scripts/release/windows/build-releases.ps1
pwsh -File scripts/lab/windows/local-lab.ps1 help
pwsh -File scripts/maintenance/windows/format-xaml.ps1 -Changed -Check
python scripts/localization/translate_resources.py --help
```

```sh
bash scripts/release/macos/build-macos-release.sh --help
bash scripts/maintenance/macos/setup-macos-debugger.sh
```

See the [release guide](../docs/releases.md), [lab guide](../tools/local-lab/README.md), and [script environment](../docs/local-script-environment.md).
The lab generator builds its helper project. It does not build, launch, or automate the Wino app.
