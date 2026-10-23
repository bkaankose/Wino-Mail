# Mail-Cal agent guidance

Mail-Cal (`hairokuma/Mail-Cal`) is a standalone fork of Wino Mail (`bkaankose/Wino-Mail`, remote `upstream`). It contacts only the mail, calendar, and contacts providers the user configures: no Wino Account, Wino Sync, AI or intelligence features, Store purchases or update checks, telemetry, or third-party lookups (Gravatar, favicon services, autodiscovery databases, DNS-over-HTTPS). Do not add code that calls any other endpoint. The macOS head was removed; Linux support is planned but the UI framework is undecided.

It is a native mail, calendar, contacts, and To Do client for Windows:

| Platform | Application | Solution | Guide |
| --- | --- | --- | --- |
| Windows | `src/MailCal.Mail.WinUI` (WinUI 3, MSIX, Native AOT) | `MailCal.slnx` | [Windows development](docs/harness/development-windows.md) |

The application uses the platform-neutral shared backend: `MailCal.Core.Domain`, `MailCal.Core`, `MailCal.Services`, `MailCal.Authentication`, `MailCal.Messages`, `MailCal.Mail.ViewModels`, `controls/MailCal.Mail.Controls.Core`, and `controls/MailCal.Editor.Core`.
Platform services live in `MailCal.Platform.Windows`. `MailCal.NotificationHost` (with `MailCal.NotificationHost.Contracts`) handles toast activation for the Windows package, and `MailCal.SourceGenerators` holds the source generators and the `MCAOT001` analyzer.

## Platform routing

- **Windows change** (WinUI head, Windows platform services, WinUI controls, XAML, MSIX, `build/MailCal.Windows.*`, Windows scripts): read the [Windows guide](docs/harness/development-windows.md) before editing. Its Native AOT and build safety section is release-blocking.
- **Shared change** (shared projects above, translations, `Directory.Build.*`, `Directory.Packages.props`, `global.json`): keep shared code free of platform UI references so a future Linux head can reuse it.

## Scope and completion

- Start with the requested behavior and named files. Preserve unrelated worktree changes.
- Complete implementation and the applicable verification in the current task.
- If the user requests review only or prohibits launch, obey that boundary and report the remaining verification.
- After a passing check, repeat it only for changed inputs, new failures, or unresolved concerns.
- Report the result, verification evidence, and remaining limits. A build alone does not prove runtime behavior.
- Run checks on the available host. Report the other platform's exact pending checks as a handoff; do not claim cross-platform completion without that evidence.
- Never commit or push unless the user explicitly asks in the current request. Creating a local branch is fine; changes stay uncommitted on the local machine until the user asks for a commit or push.

## Shared coding rules

- Treat nullable diagnostics as real defects. Guard missing items and owners explicitly; two null account IDs comparing equal does not make either account safe to dereference. Do not hide errors with null-forgiving operators, warning suppressions, disabled analyzers, or weakened Release settings.
- Give collection expressions that target read-only interfaces an explicit concrete backing type. Collections that reach an items source need a concrete static type, not an interface; `MCAOT001` enforces it. See the [implementation rules](docs/harness/implementation-rules.md#core-implementation-rules).
- Verify which source file the active project compiles before editing. Legacy and active files can share a namespace and type name.
- Keep business rules in shared services and ViewModels. Platform heads adapt them; they do not re-implement them.
- Add English strings only to `src/MailCal.Core.Domain/Translations/en_US/resources.json`.
- Shared Release builds keep their Native AOT, trimming, and warnings-as-errors gates.
- `MailCalTargetPlatform` (`Windows`; a future Linux head adds a value) and `MailCalTargetRuntimeIdentifier` are the build interface. An explicit value wins; otherwise the host decides. Platform build policy lives in `build/`; root `Directory.Build.*` holds only shared policy.

## Windows dialogs

- Every `ContentDialog` in the WinUI head is a dedicated `ContentDialog` file in `src/MailCal.Mail.WinUI/Dialogs`, with its text from `Translator`. Never write `new ContentDialog { ... }` or declare a `<ContentDialog>` inside a page or control.
- `src/MailCal.Mail.WinUI/Services/DialogService.cs` (with `DialogServiceBase`) is the only code that creates or presents dialogs. It returns the result through an async method. Pages, controls, and other services call `IMailDialogService`, `IDialogServiceBase`, or the Windows-only `IMailCalDialogService`; they never call `ShowAsync` or set `XamlRoot`.
- WinUI allows one open `ContentDialog` per `XamlRoot`. Queued dialogs go through `HandleDialogPresentationAsync`. Progress dialogs that their owner closes go through `StartSessionDialog`.

## Discovery and references

Use CodeGraph first for code discovery when `.codegraph/` exists:

```powershell
codegraph explore "named symbol or affected behavior"
```

If the result is unrelated or omits the requested script, use a targeted file read or `rg` search.
Use the changed behavior to select additional checks.
Read only references that apply to the task:

| Task | Reference |
| --- | --- |
| C#, XAML, translations, storage, or editor changes | Relevant sections of [implementation rules](docs/harness/implementation-rules.md) |
| Windows build, deployment, F5, or runtime verification | [Windows development](docs/harness/development-windows.md) |
| New Windows UI feature or visual pattern | [Mail-Cal design guideline](docs/mailcal-design-guideline.md) |
| Reusable controls or playground | [controls/AGENTS.md](controls/AGENTS.md) |
| Icons, icon fonts, or the colorful icon style | [icons/README.md](icons/README.md) and the Icons section of the [implementation rules](docs/harness/implementation-rules.md) |
| Calendar period navigation, the rail model, or the loaded event window | [calendar rail navigation](docs/calendar-rail-navigation.md) |
| Release packaging | [release guide](docs/releases.md) |
| What's New notes and illustrations | [whats-new skill](.claude/skills/whats-new/SKILL.md) |
| Scripts | [script index](scripts/README.md) |

Repository commands and package rules take precedence over stale personal skill instructions.
Use focused skills for the affected subsystem. Avoid loading overlapping general workflows for the same operation.

## Development loop

Use plain `dotnet`, `winapp`, and platform tools. Build and run commands restore dependencies by default; use the SDK's normal incremental behavior.
VS Code F5 on Windows uses the approved `scripts/development/windows/start-mailcal.ps1` helper to reuse unchanged Debug x64 output. This is the only development wrapper and input-hash cache. Keep custom debug build targets out of the project.

## Verification scope

| Change | Required evidence |
| --- | --- |
| Documentation or tooling | Valid links, command configuration, and affected script checks |
| Domain or service logic | Affected project build and manual checks in the running app when the behavior is user-visible |
| ViewModel behavior | Affected project build and manual checks for UI behavior on each affected platform |
| Windows XAML, navigation, activation, windows, or controls | Windows Debug build and manual checks in the lab |
| Localization | English source only, generated output build, other locales untouched |
| Windows package, trimming, or Native AOT | Windows Release build without launch |
| Shared production code, dependencies, or build configuration | Windows Debug x64 build and Release x64 gate |

Windows production changes always need the Release gate, run without launching or deploying:

```powershell
dotnet build src/MailCal.Mail.WinUI/MailCal.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
```

Format changed XAML before building with `scripts/maintenance/windows/format-xaml.ps1 -Changed`, and run it with `-Check` before handoff.
The repository has no automated test projects; do not add or run unit tests until the owner defines a new test project. Agent-driven UI automation is retired. The [local Docker lab](tools/local-lab/README.md) is the Windows application testing environment; use manual interaction for UI checks.
A passing build does not prove UI behavior. Report manual verification as pending unless someone performed it.

## OAuth clients (hard rule)

- **Never touch the Google UWP OAuth client `973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn`.** It is the Windows
  app's Gmail mailbox client. Do not change, rotate, replace or repurpose it. Ask the user if a task seems to need it.

## Dependencies

Package versions are managed centrally in `Directory.Packages.props`; `global.json` pins the SDK and `nuget.config` lists the feeds.
External dependencies use unconditional `PackageReference` items from nuget.org. Never substitute a local feed, local package reference, or sibling project reference.
Avoid new packages when the platform or existing libraries are sufficient, and check that a new package does not contact endpoints outside the configured providers.
Mail-Cal does not publish packages; ask before packing or pushing anything to a feed.
