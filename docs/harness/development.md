# Development commands

Wino Mail has four native applications in one repository. They share the .NET backend, ViewModels, translations, dependency versions, and the reader/editor engine. Each platform has its own build policy, tools, packaging, and operating-system integration.

| Platform | Application | Solution | Guide |
| --- | --- | --- | --- |
| Windows | `src/Wino.Mail.WinUI` (WinUI 3, MSIX) | `WinoMail.slnx` | [Windows development](development-windows.md) |
| macOS | `src/Wino.Mail.MacOS` (AppKit) | `WinoMail.MacOS.slnx` | [macOS development](development-macos.md) |
| iOS and iPadOS | `src/Wino.Mail.iOS` (UIKit, built on a Mac) | `WinoMail.iOS.slnx` | [iOS development](development-ios.md) |
| Android | `src/Wino.Mail.Android` (Android Views, built on a Mac or Windows) | `WinoMail.Android.slnx` | [Android development](development-android.md) |

Use the guide for the platform you change. A shared change, such as one in `src/Wino.Core`, `src/Wino.Services`, `src/Wino.Mail.ViewModels`, `controls/*.Core`, or translations, affects every application: follow the verification in [AGENTS.md](../../AGENTS.md) for each of them. The Apple shared layer (`Wino.Platform.Apple`, `Wino.Core.Apple.Bindings`, `controls/Wino.Editor.WebKit`) affects the macOS and iOS applications. Android-only code (`Wino.Platform.Android`, `controls/*.Android`) affects the Android application only.

## Shared commands

These run on either host from the repository root:

```sh
python scripts/localization/translate_resources.py --dry-run
python scripts/localization/validate_resources.py --dry-run
```

On macOS, use `python3`.

The build resolves `WinoTargetPlatform` from the host when it is not passed: `Windows` on Windows, `MacOS` on macOS. Explicit `-p:WinoTargetPlatform=...` wins; iOS builds always pass `-p:WinoTargetPlatform=iOS`, and Android builds always pass `-p:WinoTargetPlatform=Android`. The host never selects iOS or Android. Other hosts receive no platform defaults; pass the platform explicitly.

## Workspaces

- Opening the repository folder in VS Code keeps the Windows workflow from `.vscode`.
- [WinoMail.Windows.code-workspace](../../WinoMail.Windows.code-workspace) selects the Windows solution explicitly.
- [WinoMail.MacOS.code-workspace](../../WinoMail.MacOS.code-workspace) selects the Mac solution, its default build task, and its debugger configuration.
- [WinoMail.iOS.code-workspace](../../WinoMail.iOS.code-workspace) selects the iOS solution and its Simulator build tasks.
- [WinoMail.Android.code-workspace](../../WinoMail.Android.code-workspace) selects the Android solution and its build and emulator tasks.

## Scripts

The [script index](../../scripts/README.md) lists every script, its platform, and its shell.
