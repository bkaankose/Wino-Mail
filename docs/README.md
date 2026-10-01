# Developer documentation

These documents explain Wino Mail's development conventions, local tooling, and release process.
Commands use paths relative to the repository root unless stated otherwise.

| Document | Purpose |
| --- | --- |
| [Repository guidance](../AGENTS.md) | Project structure, development commands, implementation rules, and verification requirements. |
| [Development commands](harness/development.md) | Build diagnostics, Debug deployment, and runtime verification. |
| [Implementation rules](harness/implementation-rules.md) | Wino C#, XAML, localization, storage, and architecture conventions. |
| [Local script environment](local-script-environment.md) | Environment variables and prerequisites for release signing and translation maintenance. |
| [Local release packages](releases.md) | Build and distribute Store, Beta, and stable sideload artifacts. |
| [Wino design guideline](wino-design-guideline.md) | Interface layout, controls, themes, accessibility, and interaction conventions. |
| [Visual design reference](design-prototypes/wino-design-guideline.html) | Browser presentation of the design conventions. The Markdown guideline is the primary reference. |

Normal development uses Debug and x64. Release packages are built and inspected without installation or launch.
Official signing and publication require maintainer-granted access. Contributors can run translation dry runs and script tests without release credentials.

HTML files under `design-prototypes/` illustrate interface concepts. Their sample accounts and user-facing text represent application content, not developer setup instructions.
