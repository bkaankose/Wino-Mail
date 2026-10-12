# Development commands

Wino Mail has two native applications in one repository. They share the .NET backend, ViewModels, translations, dependency versions, and the reader/editor engine. Each platform has its own build policy, tools, packaging, and operating-system integration.

| Platform | Application | Solution | Guide |
| --- | --- | --- | --- |
| Windows | `src/Wino.Mail.WinUI` (WinUI 3, MSIX) | `WinoMail.slnx` | [Windows development](development-windows.md) |
| macOS | `src/Wino.Mail.MacOS` (AppKit) | `WinoMail.MacOS.slnx` | [macOS development](development-macos.md) |

Use the guide for the platform you change. A shared change, such as one in `src/Wino.Core`, `src/Wino.Services`, `src/Wino.Mail.ViewModels`, `controls/*.Core`, or translations, affects both applications: follow the verification in [AGENTS.md](../../AGENTS.md) for both.

## Shared commands

These run on either host from the repository root:

```sh
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug
python scripts/localization/translate_resources.py --dry-run
python scripts/localization/validate_resources.py --dry-run
```

On Windows, add `-p:Platform=x64` to test commands as the Windows guide shows. On macOS, use `python3`.

The build resolves `WinoTargetPlatform` from the host when it is not passed: `Windows` on Windows, `MacOS` on macOS. Explicit `-p:WinoTargetPlatform=...` wins. Other hosts receive no platform defaults; pass the platform explicitly.

## Workspaces

- Opening the repository folder in VS Code keeps the Windows workflow from `.vscode`.
- [WinoMail.Windows.code-workspace](../../WinoMail.Windows.code-workspace) selects the Windows solution explicitly.
- [WinoMail.MacOS.code-workspace](../../WinoMail.MacOS.code-workspace) selects the Mac solution, its default build task, and its debugger configuration.

## Scripts

The [script index](../../scripts/README.md) lists every script, its platform, and its shell.
