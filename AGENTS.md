# Wino Mail agent guidance

Wino Mail is a native mail, calendar, contacts, and To Do client with two applications in one repository:

| Platform | Application | Solution | Guide |
| --- | --- | --- | --- |
| Windows | `src/Wino.Mail.WinUI` (WinUI 3, MSIX, Native AOT) | `WinoMail.slnx` | [Windows development](docs/harness/development-windows.md) |
| macOS | `src/Wino.Mail.MacOS` (AppKit, CoreCLR) | `WinoMail.MacOS.slnx` | [macOS development](docs/harness/development-macos.md) |

Both applications use the shared backend: `Wino.Core.Domain`, `Wino.Core`, `Wino.Services`, `Wino.Authentication`, `Wino.Messaging`, `Wino.Core.ML`, `Wino.Mail.ViewModels`, `controls/Wino.Mail.Controls.Core`, and `controls/Wino.Editor.Core`.
Platform services live in `Wino.Platform.Windows` and `Wino.Platform.MacOS`. Do not change the deprecated UWP project.

## Platform routing

- **Windows change** (WinUI head, Windows platform services, WinUI controls, XAML, MSIX, `build/Wino.Windows.*`, Windows scripts): read the [Windows guide](docs/harness/development-windows.md) before editing. Its Native AOT and build safety section is release-blocking.
- **macOS change** (AppKit head, Mac platform services, AppKit controls, entitlements, `build/Wino.MacOS.*`, Mac scripts): read the [macOS guide](docs/harness/development-macos.md).
- **Shared change** (shared projects above, translations, `Directory.Build.*`, `Directory.Packages.props`, `global.json`): it affects both applications. Read both guides' verification sections, and keep shared code free of platform UI references.

## Scope and completion

- Start with the requested behavior and named files. Preserve unrelated worktree changes.
- Complete implementation and the applicable verification in the current task.
- If the user requests review only or prohibits launch, obey that boundary and report the remaining verification.
- After a passing check, repeat it only for changed inputs, new failures, or unresolved concerns.
- Report the result, verification evidence, and remaining limits. A build alone does not prove runtime behavior.
- Run checks on the available host. Report the other platform's exact pending checks as a handoff; do not claim cross-platform completion without that evidence.
- Never commit or push unless the user explicitly asks in the current request. Creating a local branch is fine; changes stay uncommitted on the local machine until the user asks for a commit or push.

## Commits and release notes

The maintainer commits straight to `main`, and release notes are drafted from those commits (`scripts/release/release.ps1 notes`). Every commit you make must carry what the notes need:

- Write the subject as the user-visible change ("Show an info bar when creating a draft fails"), not the implementation. Internal work says so plainly ("Build: …", "Docs: …").
- Add one `Refs: #N` trailer per GitHub issue the commit addresses, and run `scripts/release/release.ps1 mark N` for each, which adds the `pending release` label.
- When the user describes a bug without an issue number, search `gh issue list --search "<keywords>" --state all` and ask the user to confirm the matches before adding them.
- Add `Highlight: yes` only when the user calls the change a headline feature.
- Never use closing keywords (`Fixes #N`, `Closes #N`). Issues close when the release is published, not when the fix reaches `main`.
- Commit one issue per commit, also during bug bashes, so each fix becomes its own release-notes entry.
- For user-visible work in another repository (API, relay, website), run `scripts/release/release.ps1 add "<user-facing text>"` when the user asks for it in the notes.

## Shared coding rules

- Treat nullable diagnostics as real defects. Guard missing items and owners explicitly; two null account IDs comparing equal does not make either account safe to dereference. Do not hide errors with null-forgiving operators, warning suppressions, disabled analyzers, or weakened Release settings.
- Give collection expressions that target read-only interfaces an explicit concrete backing type. Collections that reach an items source need a concrete static type, not an interface; `WINOAOT001` enforces it. See the [implementation rules](docs/harness/implementation-rules.md#core-implementation-rules).
- Write no comments in source code unless one identifies a potential risk, explains an uncommon decision behind the code, or is needed for everyone to understand it. Do not restate member names, describe what the code plainly does, or narrate the feature.
- Verify which source file the active project compiles before editing. Legacy and active files can share a namespace and type name.
- Keep business rules in shared services and ViewModels. Platform heads adapt them; they do not re-implement them.
- Add English strings only to `src/Wino.Core.Domain/Translations/en_US/resources.json`.
- Shared Release builds keep their Native AOT, trimming, and warnings-as-errors gates. Only the AppKit projects are exempt from the AOT gate.
- `WinoTargetPlatform` (`Windows` or `MacOS`) and `WinoTargetRuntimeIdentifier` are the build interface. An explicit value wins; otherwise the host decides. Platform build policy lives in `build/`; root `Directory.Build.*` holds only shared policy.

## Windows dialogs

- Every `ContentDialog` in the WinUI head is a dedicated `ContentDialog` file in `src/Wino.Mail.WinUI/Dialogs`, with its text from `Translator`. Never write `new ContentDialog { ... }` or declare a `<ContentDialog>` inside a page or control.
- `src/Wino.Mail.WinUI/Services/DialogService.cs` (with `DialogServiceBase`) is the only code that creates or presents dialogs. It returns the result through an async method. Pages, controls, and other services call `IMailDialogService`, `IDialogServiceBase`, or the Windows-only `IWinoDialogService`; they never call `ShowAsync` or set `XamlRoot`.
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
| macOS build, signing, F5, or runtime verification | [macOS development](docs/harness/development-macos.md) |
| New Windows UI feature or visual pattern | [Wino design guideline](docs/wino-design-guideline.md) |
| New macOS UI feature or visual pattern | [macOS design decisions](docs/macos-design-decisions.md), then the [Wino design guideline](docs/wino-design-guideline.md) for shared naming and order |
| Moving duplicated Mac logic into shared code | [macOS shared-logic roadmap](docs/macos-shared-logic-roadmap.md) |
| Reusable controls or playground | [controls/AGENTS.md](controls/AGENTS.md) |
| Icons, icon fonts, or the colorful icon style | [icons/README.md](icons/README.md) and the Icons section of the [implementation rules](docs/harness/implementation-rules.md) |
| Intelligence jobs, artifacts, or the daily briefing | [mail intelligence](docs/mail-intelligence.md) |
| Calendar period navigation, the rail model, or the loaded event window | [calendar rail navigation](docs/calendar-rail-navigation.md) |
| Release packaging | [release guide](docs/releases.md) |
| Release notes, GitHub releases, or marking issues for a release | [release skill](.claude/skills/release/SKILL.md) and [release notes format](release-notes/README.md) |
| What's New notes and illustrations | [whatsnew skill](.claude/skills/whatsnew/SKILL.md) |
| Scripts | [script index](scripts/README.md) |

Repository commands and package rules take precedence over stale personal skill instructions.
Use focused skills for the affected subsystem. Avoid loading overlapping general workflows for the same operation.

## Development loop

Use plain `dotnet`, `winapp`, and platform tools. Build and run commands restore dependencies by default; use the SDK's normal incremental behavior.
VS Code F5 on Windows uses the approved `scripts/development/windows/start-wino.ps1` helper to reuse unchanged Debug x64 output. This is the only development wrapper and input-hash cache. Keep custom debug build targets out of the project.
Mac F5 prepares the debugger, then runs a plain `dotnet build`.

## Verification scope

| Change | Required evidence |
| --- | --- |
| Documentation or tooling | Valid links, command configuration, and affected script checks |
| Domain or service logic | Affected project build and manual checks in the running app when the behavior is user-visible |
| ViewModel behavior | Affected project build and manual checks for UI behavior on each affected platform |
| Windows XAML, navigation, activation, windows, or controls | Windows Debug build and manual checks in the lab |
| AppKit views, navigation, activation, or controls | Native Debug arm64 and x64 builds and manual checks on the Mac |
| Localization | English source only, generated output build, other locales untouched |
| Windows package, trimming, or Native AOT | Windows Release build without launch |
| macOS signing, entitlements, or release inputs | Native Release arm64 and x64 builds and bundle identity, signature, and entitlement checks |
| Shared production code, dependencies, or build configuration | Windows Debug x64 build and Release x64 gate, and the macOS builds as a handoff when no Mac is available |

Windows production changes always need the Release gate, run without launching or deploying:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
```

Format changed XAML before building with `scripts/maintenance/windows/format-xaml.ps1 -Changed`, and run it with `-Check` before handoff.
The repository has no automated test projects; do not add or run unit tests until the owner defines a new test project. Agent-driven UI automation is retired. The [local Docker lab](tools/local-lab/README.md) is the Windows application testing environment; use manual interaction for UI checks.
A passing build does not prove UI behavior. Report manual verification as pending unless someone performed it.

## Accounts, OAuth clients and signing (hard rules)

Binding for every agent. Backend counterpart: `AGENTS.md` and `docs/social-sign-in-setup.md` in Wino-Mail-API.

- **Never touch the Google UWP OAuth client `973025879644-s7b4ur9p3rlgop6a22u7iuptdc0brnrn`.** It is the Windows
  app's Gmail mailbox client. Do not change, rotate, replace or repurpose it, and never use it for Wino Accounts
  sign-in. Ask the user if a task seems to need it.
- Wino Account sign-in with Microsoft, Google and Apple on Windows always uses the API's browser flow: the shared
  `WinoAccountExternalSignInService` (`Wino.Services`) opens `WinoAccountExternalAuth.StartUri` in the default browser
  through `WindowsBrowserAuthBroker` (`Wino.Platform.Windows`), and the `wino://auth-complete` protocol activation
  hands the completion code back through `WindowsExternalAuthCallbacks`. No native provider SDK is involved on Windows.
- The browser flow's callback address (`wino://auth-complete`, the API's `Secrets:ExternalAuth:AppCallbackUri`)
  is spelled once on the client, in `WinoAccountExternalAuth` (`Wino.Core.Domain`). Change it there and in the API together.
- The production API starts the flow on `https://winomail.app/auth/{provider}`; a local API is opened directly at
  `api/v1/auth/{provider}/authorize` (`WinoAccountApiClient.GetExternalSignInAuthorizationUri`).

## Dependencies

Both applications share `Directory.Packages.props`, `global.json`, and `nuget.config`.
Published cross-repository dependencies must use unconditional `PackageReference` items.
If a dependency change requires publication, audit and publish the version, then update every consumer.
Publish without asking for approval. When `NUGET_API_KEY` is set in the local environment, pack and push the package to nuget.org yourself. This does not need a git commit or push.
Never substitute a local feed, local package reference, or sibling project reference.
Wait until nuget.org indexes the version before you restore consumers.
Never print or log the key.
