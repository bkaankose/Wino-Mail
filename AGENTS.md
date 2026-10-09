# Wino Mail agent guidance

Wino Mail is a native Windows mail client. The active app is `src/Wino.Mail.WinUI`, in `WinoMail.slnx`.
Do not change the deprecated UWP project.

## Scope and completion

- Start with the requested behavior and named files. Preserve unrelated worktree changes.
- Complete implementation and the applicable verification in the current task.
- If the user requests review only or prohibits launch, obey that boundary and report the remaining verification.
- After a passing check, repeat it only for changed inputs, new failures, or unresolved concerns.
- Report the result, verification evidence, and remaining limits. A build alone does not prove runtime behavior.

## Native AOT and build safety — critical

Native AOT compatibility is a release-blocking correctness requirement. A binding that works in Debug can fail when users open a page in Release. Prevent these failures during implementation; do not rely on users or Sentry to discover them.

- Prefer compiled `x:Bind` for application properties and commands, including `CopyClipboardCommand`. Do not introduce runtime `{Binding}` paths where compiled bindings suffice.
- When runtime bindings are necessary, audit every custom CLR object traversed by the path. For `ViewModel.Property`, both the page's `ViewModel` getter and the ViewModel's property need generated binding support. Use narrowly scoped `WinRT.GeneratedBindableCustomProperty` annotations on partial types, guarded by `WINRT_EXPOSED` in shared Windows/portable code. Framework and dependency-property bindings do not require blanket annotations.
- Include bindings inside templates and bindings created with `SetBinding` in the audit. For new or changed generated binding support, inspect the generated accessors as well as the build result. Annotating only the first object in a multi-part path is insufficient.
- Give WinRT-facing collections explicit concrete backing types. Use an array or `List<T>`, or an explicit array cast such as `(Option[])[...]`; never leave collection expressions targeting read-only interfaces with an implicit compiler-generated backing type. See the [implementation rules](docs/harness/implementation-rules.md#core-implementation-rules).
- Treat nullable diagnostics as real defects. Guard missing items and owners explicitly; two null account IDs comparing equal does not make either account safe to dereference. Do not hide errors with null-forgiving operators, warning suppressions, disabled analyzers, or weakened Release settings.
- Verify which source file the active project compiles before editing. Legacy and active files can share a namespace and type name.
- Changes to production C#, XAML, bindings, WinRT-facing models, dependencies, or build configuration require both the Debug x64 build and the Release x64 gate below, in addition to applicable tests. Documentation-only changes do not require builds. Fix introduced errors and related build blockers before handoff; if an unrelated blocker prevents verification, report the failed check and do not claim Release/AOT validation passed.

Run the Release gate without launching or deploying:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:GenerateAppxPackageOnBuild=false
```

Keep manual lab verification separate: successful compilation and generated accessors do not prove runtime behavior.

## Discovery and references

Use CodeGraph first for code discovery when `.codegraph/` exists:

```powershell
codegraph explore "named symbol or affected behavior"
```

If the result is unrelated or omits the requested script, use a targeted file read or `rg` search.
CodeGraph results can miss tests. Use the changed behavior to select additional checks.
Read only references that apply to the task:

| Task | Reference |
| --- | --- |
| C#, XAML, translations, storage, or editor changes | Relevant sections of [implementation rules](docs/harness/implementation-rules.md) |
| Build failure, deployment, or runtime verification | Relevant sections of [development commands](docs/harness/development.md) |
| New UI feature or visual pattern | [Wino design guideline](docs/wino-design-guideline.md) |
| Reusable controls or playground | [controls/AGENTS.md](controls/AGENTS.md) |
| Icons, icon fonts, or the colorful icon style | [icons/README.md](icons/README.md) and the Icons section of the [implementation rules](docs/harness/implementation-rules.md) |
| Intelligence jobs, artifacts, or the daily briefing | [mail intelligence](docs/mail-intelligence.md) |
| Release packaging | [release guide](docs/releases.md) |
| What's New notes and illustrations | [whats-new skill](.claude/skills/whats-new/SKILL.md) |

Repository commands and package rules take precedence over stale personal skill instructions.
Use focused skills for the affected subsystem. Avoid loading overlapping general workflows for the same operation.

## Development loop

Use plain `dotnet` and `winapp` commands. Use Debug and x64 for development:

```powershell
dotnet build src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj -c Debug -p:Platform=x64 -p:RuntimeIdentifier=win-x64
dotnet test tests/Wino.Core.Tests/Wino.Core.Tests.csproj -c Debug -p:Platform=x64 --filter "FullyQualifiedName~RelevantTestClass"
winapp run src/Wino.Mail.WinUI/Wino.Mail.WinUI.csproj --arch x64 --detach
```

Build and run commands restore dependencies by default. Use the SDK's normal incremental behavior.
VS Code F5 uses the approved `scripts/development/start-wino.ps1` helper to reuse unchanged Debug x64 output.
This is the only development wrapper and input-hash cache. Keep custom debug build targets out of the project.
Release packaging retains its scripts and targets under `scripts/release`.
See [development commands](docs/harness/development.md) for VS Code F5 and package troubleshooting.
See [script categories](scripts/README.md) for maintenance, localization, release, and lab commands.

## Package and runtime boundaries

- Keep the app packaged as MSIX with the checked-in manifest identity and publisher.
- Use WinApp CLI 0.7+ project mode for development deployment and activation.
- Preserve application data. Do not use `--clean`, unregister packages, or create another development identity.
- Before deployment, compare the installed package identity and publisher with the manifest. Stop on a mismatch or a signed non-development installation.
- Close the Debug application, including its tray process, before deployment if it holds package files open.
- Never launch the packaged executable directly. Release validation compiles and packages without launching.
- Agent-driven UI automation is retired. Do not create or run UI test scripts or require `winapp ui` audits.
- The [local Docker lab](tools/local-lab/README.md) is the application testing environment. Use manual interaction for UI checks.
- Keep unit tests and release-script tests. Report manual verification as pending unless someone performed it.

## Verification scope

The Native AOT and build safety requirements above supplement this table.

| Change | Required evidence |
| --- | --- |
| Documentation or tooling | Valid links, command configuration, and affected script checks |
| Domain or service logic | Affected project build and directly affected unit tests |
| ViewModel behavior | Affected unit tests and manual lab checks for UI behavior |
| XAML, navigation, activation, windows, or controls | Debug build and manual checks in the lab |
| Localization | English source only, generated output build, other locales untouched |
| Package, trimming, or Native AOT | Release build without launch, plus release-script tests when applicable |

Format changed XAML before building with `scripts/maintenance/format-xaml.ps1 -Changed`.
Run the same command with `-Check` before handoff.
A passing build does not prove UI behavior. State which manual checks remain.

Published cross-repository dependencies must use unconditional `PackageReference` items.
If a dependency change requires publication, audit and publish the version, then update every consumer.
Publish without asking for approval. When `NUGET_API_KEY` is set, pack and push the package locally. This does not need a git commit or push. Never print or log the key.
Never substitute a local feed, local package reference, or sibling project reference.
When `NUGET_API_KEY` is set in the local environment, pack and push the package to nuget.org yourself.
Then wait until nuget.org indexes the version before you restore consumers.
Never print or log the key.
