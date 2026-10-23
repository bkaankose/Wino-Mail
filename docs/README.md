# Developer documentation

These documents explain Mail-Cal's development conventions, local tooling, and release process for the Windows application.
Commands use paths relative to the repository root unless stated otherwise.

| Document | Platform | Purpose |
| --- | --- | --- |
| [Repository guidance](../AGENTS.md) | Both | Project map, shared rules, platform routing, and verification requirements. |
| [Development commands](harness/development.md) | Both | Platform selector, shared commands, and workspaces. |
| [Windows development](harness/development-windows.md) | Windows | WinUI, Native AOT, MSIX, WinApp, F5, Sandbox, XAML formatting, and lab checks. |
| [Implementation rules](harness/implementation-rules.md) | Both | C#, localization, storage, and architecture conventions, plus WinUI XAML rules. |
| [Local script environment](local-script-environment.md) | Both | Environment variables and prerequisites for release signing and translation maintenance. |
| [Release packages](releases.md) | Windows | Shared release steps, with the [Windows](releases-windows.md) guide. |
| [Mail-Cal design guideline](mailcal-design-guideline.md) | Windows, shared parity | Interface layout, controls, themes, accessibility, and interaction conventions. |
| [Visual design reference](design-prototypes/mailcal-design-guideline.html) | Windows | Browser presentation of the design conventions. The Markdown guideline is the primary reference. |

The [local Docker lab](../tools/local-lab/README.md) currently runs on Windows only.
Historical records, such as the [AppKit migration plan](appkit-platform-migration-plan.md) and the [AppKit foundation validation](harness/appkit-foundation-validation.md), describe earlier decisions and evidence; the guides above are current.

Normal Windows development uses Debug and x64. Release packages are built and inspected without installation or launch.
Official signing and publication require maintainer-granted access. Contributors can run translation dry runs without release credentials.

HTML files under `design-prototypes/` illustrate interface concepts. Their sample accounts and user-facing text represent application content, not developer setup instructions.
