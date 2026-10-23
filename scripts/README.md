# Repository scripts

Scripts are grouped by purpose, then by platform. Run them from any directory; each resolves the repository root from its own location.
Terminal builds and launches use plain `dotnet`, `winapp`, and platform tools. VS Code F5 on Windows uses one PowerShell helper to decide whether WinApp needs to build.
See the [Windows development guide](../docs/harness/development-windows.md).

| Category | Script | Platform | Shell | Purpose |
| --- | --- | --- | --- | --- |
| Development | `development/windows/start-mailcal.ps1` | Windows | PowerShell 7.2+ | Check build inputs and output, then build if needed and launch the packaged Debug app |
| Release | `release/windows/build-releases.ps1` | Windows | PowerShell 7 | Optionally translate resources, then build and package Store, Beta, and sideload releases |
| Release | `release/windows/Install-MailCal.ps1` | Windows | PowerShell 5.1+ | Shipped with GitHub releases: trust the self-signed Mail-Cal certificate, then install or update the bundle |
| Release | `release/whats-new/validate.ps1` | Windows | PowerShell 7 | Validate release notes and illustration references |
| Lab | `lab/windows/local-lab.ps1` | Windows | PowerShell 7 | Start five Docker IMAP/SMTP/DAV servers, seed and verify them, generate test account data, and install it into the Debug app |
| Maintenance | `maintenance/windows/format-xaml.ps1` | Windows | PowerShell 7 | Format WinUI XAML or check formatting with the pinned XAML Styler |
| Maintenance | `maintenance/windows/audit-xaml-accessibility.ps1` | Windows | PowerShell | Report XAML controls without accessible labels |
| Maintenance | `maintenance/windows/audit-xaml-icons.ps1` | Windows | PowerShell | Check use of the Mail-Cal icon system in WinUI code |
| Maintenance | `maintenance/windows/build-notification-icons.ps1` | Windows | PowerShell | Render toast button icons with theme, contrast, and scale variants; `-AttributionLogos` adds missing app logo target sizes to the app and the beta artwork |
| Localization | `localization/translate_resources.py` | Any | Python 3.10+ | Generate translations from English resources |
| Localization | `localization/validate_resources.py` | Any | Python 3.10+ | Audit or repair translations |

`release/windows/profiles` contains release channel configuration.
`release/windows/MailCal.Release.targets` exports the release payload used by the Windows release script.
`release/Assets/Beta` contains the Windows beta artwork.
The root `Directory.Build.targets` retains shared compiler and Native AOT rules; `build/MailCal.Windows.targets` holds the Windows-only packaging rules.

Native-only scripts check the host first and stop on the wrong platform.

## Examples

```powershell
pwsh -File scripts/release/windows/build-releases.ps1
pwsh -File scripts/lab/windows/local-lab.ps1 help
pwsh -File scripts/maintenance/windows/format-xaml.ps1 -Changed -Check
python scripts/localization/translate_resources.py --help
```

See the [release guide](../docs/releases.md), [lab guide](../tools/local-lab/README.md), and [script environment](../docs/local-script-environment.md).
The lab generator builds its helper project. Only the explicit `install-app` command builds, deploys, and launches the Mail-Cal app; nothing automates its UI.
