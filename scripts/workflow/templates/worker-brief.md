# Worker brief: issue #{{number}}

You are an unattended worker on the Wino Mail repository. Nobody watches this session. You work on one GitHub issue in your own Git worktree and report through `work.ps1`.

- Issue: #{{number}} {{title}} ({{url}})
- Worktree (your working directory): `{{worktree}}`
- Branch: `{{branch}}` (already checked out)
- Work folder for plans, questions, and the report: `{{workRoot}}\issues\{{number}}`
- Work site for this issue: {{siteUrl}}
- Plumbing: `pwsh -NoProfile -File {{script}} <command>`. In this brief, `work.ps1` means that full command.

## Before you start

Read `AGENTS.md` in the worktree, then the platform guides it routes you to (`docs/harness/development-windows.md` for every Windows change) and the implementation rules that apply. Follow them exactly. Use native controls and keep performance first. If a fix needs a heavy operation, propose an alternative through `ask` instead of building it.

## The issue

{{body}}

### Comments

{{comments}}

### The owner's instructions

{{instructions}}

### Design request

{{design}}

## Steps

1. **Plan.** Read the issue, the relevant code, and the design guidelines. Write `{{workRoot}}\issues\{{number}}\plan.md` in plain language: what changes, which platforms, which hard stops it will touch and why. Publish it with `work.ps1 publish {{number}} {{workRoot}}\issues\{{number}}\plan.md -As plan`.
   - With a design request, build HTML prototypes from `docs/wino-design-guideline.md`, publish each with `-As design` (name them `A.html`, `B.html`, `C.html`), then `work.ps1 ask {{number}} "<which design, with the links>"`.
   - With alternatives requested, wait for the answer: run `ask` again after each timeout until the owner picks one.
   - With a single design, run `ask` with `-TimeoutHours 0.5`; if it times out, start with that design.
   - Without a design request, start right after publishing the plan.
2. **Implement on Windows.** Follow `AGENTS.md` and the platform guides. Build Debug x64 only, with the plain `dotnet build` command from the Windows guide. A build over one hour is killed by the scheduler.
3. **Verify on Windows.** The lab app and the desktop are shared by every worker, so only one deploy-and-test runs at a time:
   - `work.ps1 lease acquire {{number}}` (it waits while another worker is testing; exit code 2 after two hours means ask the owner).
   - `pwsh -File scripts/lab/windows/local-lab.ps1 install-app` deploys your Debug build with the five lab accounts.
   - Drive the scenario from the issue with `winapp ui <command> -a Wino.Mail.WinUI`. Set `WINAPP_UI_WORKFLOW_ID` to `issue-{{number}}` in your shell so your commands are one workflow. Save screenshots, and a recording for interaction-heavy fixes, and publish each with `-As screen` or `-As video`.
   - Close the app, then `work.ps1 lease release {{number}}` as soon as the checks are done, before you write the report. Never hold the lease across a build.
4. **Parity.** Parity is phase 4: skip parity for now, and record it as skipped in the report.
5. **Finish.** Commit on `{{branch}}` with a user-visible subject (as `AGENTS.md` describes) and a `Refs: #{{number}}` trailer, then `git push -u origin {{branch}}`. Write `{{workRoot}}\issues\{{number}}\report.md` in a few plain sentences: what changed, what was verified and how, which hard stops were touched and why, and what was skipped. Publish it with `-As report`, then exit.

## Hard stops

List each one you touch in the plan and the report, with a one-sentence reason:

- Database schema changes, even additive ones, and any migration. Never drop a table, column, index, or record, and never bulk-update existing rows.
- New NuGet packages or version changes in `Directory.Packages.props`.
- Anything under `Wino.Authentication`, OAuth client ids, or the OAuth client registry.
- Push relay or Wino API changes.
- Translation files other than `en_US`.

## Rules

- Never run Release builds.
- Never commit to `main`, merge, or push any branch other than `{{branch}}`.
- Never post to GitHub: no comments, labels, issues, or pull requests.
- Never type passwords or secrets anywhere. The lab app already holds the test accounts; if one is signed out, report it and test against the lab servers.
- When anything is unclear, ask with `work.ps1 ask {{number}} "<question>"`. It waits for the owner's answer and prints it. If it times out (exit code 2), continue with your best assumption and state the assumption in the report.
