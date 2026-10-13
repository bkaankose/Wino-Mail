# Development commands

Wino Mail has three native applications in one repository. They share the .NET backend, ViewModels, translations, dependency versions, and the reader/editor engine. Each platform has its own build policy, tools, packaging, and operating-system integration.

| Platform | Application | Solution | Guide |
| --- | --- | --- | --- |
| Windows | `src/Wino.Mail.WinUI` (WinUI 3, MSIX) | `WinoMail.slnx` | [Windows development](development-windows.md) |
| macOS | `src/Wino.Mail.MacOS` (AppKit) | `WinoMail.MacOS.slnx` | [macOS development](development-macos.md) |
| iOS and iPadOS | `src/Wino.Mail.iOS` (UIKit, built on a Mac) | `WinoMail.iOS.slnx` | [iOS development](development-ios.md) |

Use the guide for the platform you change. A shared change, such as one in `src/Wino.Core`, `src/Wino.Services`, `src/Wino.Mail.ViewModels`, `controls/*.Core`, or translations, affects every application: follow the verification in [AGENTS.md](../../AGENTS.md) for each of them. The Apple shared layer (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `controls/Wino.Editor.WebKit`) affects the macOS and iOS applications.

## Shared commands

These run on either host from the repository root:

```sh
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug
python scripts/localization/translate_resources.py --dry-run
python scripts/localization/validate_resources.py --dry-run
```

On Windows, add `-p:Platform=x64` to test commands as the Windows guide shows. On macOS, use `python3`.

The build resolves `WinoTargetPlatform` from the host when it is not passed: `Windows` on Windows, `MacOS` on macOS. Explicit `-p:WinoTargetPlatform=...` wins; iOS builds always pass `-p:WinoTargetPlatform=iOS`. Other hosts receive no platform defaults; pass the platform explicitly.

## Workspaces

- Opening the repository folder in VS Code keeps the Windows workflow from `.vscode`.
- [WinoMail.Windows.code-workspace](../../WinoMail.Windows.code-workspace) selects the Windows solution explicitly.
- [WinoMail.MacOS.code-workspace](../../WinoMail.MacOS.code-workspace) selects the Mac solution, its default build task, and its debugger configuration.
- [WinoMail.iOS.code-workspace](../../WinoMail.iOS.code-workspace) selects the iOS solution and its Simulator build tasks.

## Scripts

The [script index](../../scripts/README.md) lists every script, its platform, and its shell.
