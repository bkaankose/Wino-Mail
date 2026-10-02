# Repository scripts

Run commands from the repository root. Terminal builds and launches use plain `dotnet` and `winapp` commands.
VS Code F5 uses one PowerShell helper to select whether WinApp needs to build.
See [development commands](../docs/harness/development.md) for VS Code F5.

| Category | Script | Purpose |
| --- | --- | --- |
| Development | `development/start-wino.ps1` | Check build inputs and output, then build if needed and launch the packaged Debug app |
| Release | `release/build-releases.ps1` | Build and package Store, Beta, and sideload releases |
| Release | `release/upload-sentry-symbols.ps1` | Upload symbols for a selected release |
| Release | `release/whats-new/validate.ps1` | Validate release notes and illustration references |
| Lab | `lab/local-lab.ps1` | Provision Docker mail/DAV servers and generate test account data |
| Maintenance | `maintenance/format-xaml.ps1` | Format XAML or check formatting with the pinned XAML Styler |
| Maintenance | `maintenance/audit-xaml-accessibility.ps1` | Report XAML controls without accessible labels |
| Maintenance | `maintenance/audit-xaml-icons.ps1` | Check use of the Wino icon system |
| Localization | `localization/translate_resources.py` | Generate translations from English resources |
| Localization | `localization/validate_resources.py` | Audit or repair translations |

`release/profiles` contains release channel configuration.
`release/Wino.Release.targets` exports the release payload used by the release script.
The root `Directory.Build.targets` retains shared compiler settings and Native AOT release packaging rules.

## Examples

```powershell
pwsh -File scripts/release/build-releases.ps1
pwsh -File scripts/lab/local-lab.ps1 help
pwsh -File scripts/maintenance/format-xaml.ps1 -Changed -Check
python scripts/localization/translate_resources.py --help
```

See the [release guide](../docs/releases.md), [lab guide](../tools/local-lab/README.md), and [script environment](../docs/local-script-environment.md).
The lab generator builds its helper project. It does not build, launch, or automate the Wino app.
