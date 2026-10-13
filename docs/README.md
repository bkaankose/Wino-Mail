# Developer documentation

These documents explain Wino Mail's development conventions, local tooling, and release process for the Windows, macOS, and iOS applications.
Commands use paths relative to the repository root unless stated otherwise.

| Document | Platform | Purpose |
| --- | --- | --- |
| [Repository guidance](../AGENTS.md) | All | Project map, shared rules, platform routing, and verification requirements. |
| [Development commands](harness/development.md) | Windows, macOS | Platform selector, shared commands, and workspaces. |
| [Windows development](harness/development-windows.md) | Windows | WinUI, Native AOT, MSIX, WinApp, F5, Sandbox, XAML formatting, and lab checks. |
| [macOS development](harness/development-macos.md) | macOS | SDK and Xcode, AppKit, CoreCLR, signing, sandbox, Keychain, storage, activation, and F5. |
| [iOS development](harness/development-ios.md) | iOS, iPadOS | iOS workload and Simulator, UIKit, signing, entitlements, Keychain, storage, activation, and the Debug bridge. |
| [Implementation rules](harness/implementation-rules.md) | All | C#, localization, storage, and architecture conventions, plus WinUI XAML rules. |
| [Local script environment](local-script-environment.md) | Windows, macOS | Environment variables and prerequisites for release signing and translation maintenance. |
| [Release packages](releases.md) | Windows, macOS | Shared release steps, with the [Windows](releases-windows.md) and [macOS](releases-macos.md) guides. |
| [Wino design guideline](wino-design-guideline.md) | Windows, shared parity | Interface layout, controls, themes, accessibility, and interaction conventions. |
| [macOS design decisions](macos-design-decisions.md) | macOS | Authoritative AppKit shell, workflow, and control decisions. |
| [iOS design decisions](ios-design-decisions.md) | iOS, iPadOS | iPhone and iPad shell decisions, differences from the Mac, project map, and implementation status. |
| [macOS shared-logic roadmap](macos-shared-logic-roadmap.md) | All | Phased plan for moving duplicated Mac logic into shared code. |
| [Visual design reference](design-prototypes/wino-design-guideline.html) | Windows | Browser presentation of the design conventions. The Markdown guideline is the primary reference. |

The [local Docker lab](../tools/local-lab/README.md) currently runs on Windows only.
Historical records, such as the [AppKit migration plan](appkit-platform-migration-plan.md) and the [AppKit foundation validation](harness/appkit-foundation-validation.md), describe earlier decisions and evidence; the guides above are current.

Normal Windows development uses Debug and x64; normal Mac development uses Debug on Apple Silicon; normal iOS development uses Debug in the Simulator on Apple Silicon. Release packages are built and inspected without installation or launch.
Official signing and publication require maintainer-granted access. Contributors can run translation dry runs and script tests without release credentials.

HTML files under `design-prototypes/` illustrate interface concepts. Their sample accounts and user-facing text represent application content, not developer setup instructions.
