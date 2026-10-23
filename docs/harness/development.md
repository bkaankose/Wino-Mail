# Development commands

Mail-Cal has two native applications in one repository. They share the .NET backend, ViewModels, translations, dependency versions, and the reader/editor engine. Each platform has its own build policy, tools, packaging, and operating-system integration.

| Platform | Application | Solution | Guide |
| --- | --- | --- | --- |
| Windows | `src/MailCal.Mail.WinUI` (WinUI 3, MSIX) | `MailCal.slnx` | [Windows development](development-windows.md) |

Use the guide for the platform you change. A shared change, such as one in `src/MailCal.Core`, `src/MailCal.Services`, `src/MailCal.Mail.ViewModels`, `controls/*.Core`, or translations, affects both applications: follow the verification in [AGENTS.md](../../AGENTS.md) for both.

## Shared commands

These run on either host from the repository root:

```sh
python scripts/localization/translate_resources.py --dry-run
python scripts/localization/validate_resources.py --dry-run
```


The build resolves `MailCalTargetPlatform` from the host when it is not passed: `Windows` on Windows. Explicit `-p:MailCalTargetPlatform=...` wins. Other hosts receive no platform defaults; pass the platform explicitly.

## Workspaces

- Opening the repository folder in VS Code keeps the Windows workflow from `.vscode`.
- [MailCal.Windows.code-workspace](../../MailCal.Windows.code-workspace) selects the Windows solution explicitly.

## Scripts

The [script index](../../scripts/README.md) lists every script, its platform, and its shell.
