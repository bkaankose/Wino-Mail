# Wino Mail agent guidance

Wino Mail is a native Windows mail client. The active app is `src/Wino.Mail.WinUI`, in `WinoMail.slnx`.
Do not change the deprecated UWP project.

## Scope and completion

- Start with the requested behavior and named files. Preserve unrelated worktree changes.
- Complete implementation and the applicable verification in the current task.
- If the user requests review only or prohibits launch, obey that boundary and report the remaining verification.
- After a passing check, repeat it only for changed inputs, new failures, or unresolved concerns.
- Report the result, verification evidence, and remaining limits. A build alone does not prove runtime behavior.

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
| Contacts, To Do, or activation audit replay | [replay contract](scripts/ui-audit/REPLAY.md) |
| Intelligence jobs, artifacts, or the daily briefing | [mail intelligence](docs/mail-intelligence.md) |
| Release packaging | [release guide](docs/releases.md) |
| What's New notes and illustrations | [whats-new skill](.claude/skills/whats-new/SKILL.md) |

Repository commands and package rules take precedence over stale personal skill instructions.
Use focused skills for the affected subsystem. Avoid loading overlapping general workflows for the same operation.

## Development loop

Use Debug and x64 for normal development:

```powershell
.\scripts\wino.ps1 affected -Path src/Wino.Services/MailService.cs
.\scripts\wino.ps1 build app
.\scripts\wino.ps1 test core -Filter "FullyQualifiedName~RelevantTestClass"
.\scripts\wino.ps1 xaml changed
.\scripts\wino.ps1 xaml changed -Check
```

Before runtime work, use `./scripts/wino.ps1 doctor app` to detect package ownership conflicts before a build or process shutdown.
For scripted task/contact persistence checks, use `audit app -List` and [regression instructions](scripts/ui-audit/REGRESSION.md).

`affected` without `-Path` includes all tracked and untracked changes.
Use task-specific paths in a dirty worktree. Its output helps select tests but does not prove coverage.
`build core` means `Wino.Mail.Controls.Core`; `test core` means `Wino.Core.Tests`.
Use `help` for all targets.

Restore after package, project, framework, or runtime inputs change, or when required restore assets are missing.
For repeated tests, use `-NoBuild` only while the same test output and dependencies remain current.
For package, trimming, or Native AOT changes, run `build app -Configuration Release` without deployment.
Format changed XAML before building. The pinned XAML Styler check must pass.

## Package and runtime boundaries

- Use WinApp CLI 0.6+ project mode with the checked-in manifest and existing Debug package family.
- Before deployment, compare the installed package name and publisher with the manifest. Stop on a mismatch.
- Immediately before each live app test, force-stop any running process for the checked-in Debug app after the doctor and package identity checks, then launch the current Debug build with WinApp CLI project mode. Do not wait for a graceful shutdown or ask for confirmation.
- Preserve application data. Never create another identity, use folder mode, clean, or unregister the package.
- Never launch the packaged executable directly. Never deploy, launch, or UI-test Release.
- Use only `winapp ui` for application interaction and visual evidence, including the playground.
- A screenshot alone is not an interaction test. Report the action, assertion, process or HWND, and theme.
- Establish current-source deployment before claiming runtime verification. Otherwise report it as pending.

## Verification scope

| Change | Required evidence |
| --- | --- |
| Documentation or harness | Links, command help, and affected script checks. No app build for prose alone. |
| Domain or service logic | Affected project build and directly affected unit tests |
| ViewModel or messenger behavior | Affected tests and UI-bound state through the current Debug app |
| XAML, code-behind, navigation, activation, windows, or controls | Current Debug deployment, automation-ID audit, and affected WinApp interaction |
| Reusable controls | Playground states and themes required by `controls/AGENTS.md` |
| Localization | English source only, generated output build, other locales untouched |
| Package, trimming, or Native AOT | Compile-only Release build. Runtime checks use Debug. |

Published cross-repository dependencies must use unconditional `PackageReference` items.
If a dependency change requires publication, audit and publish the version, then update every consumer.
Never substitute a local feed or sibling project reference.
