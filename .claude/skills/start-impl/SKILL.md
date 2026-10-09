---
name: start-impl
description: Plan and begin resumable multi-item work with an approved agreement and a compact repository handoff record. Use for start-impl requests covering fixes, implementation, research, or requested subagents; use continue-impl to resume an existing work ID.
---

# Start resumable work

Invocation: `/start-impl <request>`. Slash-command registration is harness-specific; the instructions also work when this file is supplied directly. This skill is self-contained and requires only repository read/write access and the tools needed for the actual task.

## Start workflow

1. Read applicable repository instructions and inspect the affected code or materials. Identify the repository root, current branch/commit, and existing changes to preserve. Do not treat the request's examples as additional tasks.
2. Plan before implementing. Resolve material questions about scope, decisions, dependencies, completion criteria, and verification. Capture requested subagent assignments, models, and effort. Check relevant capabilities early; do not promise unavailable execution or that future blockers cannot arise.
3. Generate a work ID using the shared protocol below. When writes are permitted, create a draft record and save useful planning progress. Show the ID and `/continue-impl <work-id>` once the record exists. If native Plan Mode forbids writes, keep planning in chat and explicitly state that the record is not saved yet; do not bypass that restriction.
4. Present a decision-complete plan and obtain the user's agreement before implementation. Existing explicit approval of that plan counts; do not request it again. Record the approval's date and a short user-message reference or quotation. Draft records must say `Draft` and must not imply approval.
5. After approval, and only in a writable mode, persist the approved agreement before changing implementation files. If no record exists yet, create it now and report its ID and resume command. An approval does not itself disable a harness's Plan Mode.
6. Follow the shared execution protocol until the work is complete or cannot progress. Make routine implementation choices independently. If new evidence requires changing agreed scope or a material decision, ask a targeted question and block only dependent work; preserve the existing agreement until the change is agreed.

## Shared work-record protocol: impl-work/v1

This section is intentionally identical in both skills so either file can be shared alone. Keep the protocol sections aligned when editing them.

### Location and identity

- Use the current repository root, never a global account directory. Store state at `iq/<work-id>.md` and substantial findings, evidence, or subagent reports under `iq/<work-id>/` only when needed.
- New IDs use `YYYYMMDD-short-slug-xxxxxx`, with a lowercase hyphenated slug and six random hexadecimal characters. Check both paths for collisions and regenerate the suffix rather than overwriting existing work.
- Accept IDs matching `^[a-z0-9]+(?:-[a-z0-9]+)*$` only. Resolve the record beneath the repository's `iq` directory; do not interpret the ID as a path or shell expression. Reject path redirection outside the repository.
- Use repository-relative context paths and links relative to the work record. Keep substantive results in files, not only in a conversation or inaccessible session. Never store credentials or private message contents unnecessarily.
- Same-checkout, sequential handoff is the default. These skills do not synchronize code, switch accounts, change harness configuration, or automatically commit/push. A different checkout needs the corresponding code and records transferred explicitly before resumption.

### Compact record template

Replace the placeholders with actual facts. Use `none` or `not applicable` where appropriate; do not invent approval, evidence, or session identifiers.

```markdown
# <work-id>: <short title>
Protocol: impl-work/v1
Owner: <harness/session label>; <active or released>
Updated: <ISO 8601 timestamp with timezone>

## Purpose
<Outcome and scope boundaries, in a few sentences.>

## Agreement
Plan: <Draft or Approved>
Approval: <pending, or date and user-message reference/short quotation>
- Plan: <ordered approach and agreed deliverables>
- Decisions/constraints: <only choices that affect execution>

## Context
- Checkout: <branch or detached HEAD>; start commit: <hash or not applicable>
- Relevant files/materials: <paths or sources>
- Existing changes to preserve: <paths and brief distinction from this work>
- Verification: <necessary commands/manual checks and relevant environment>

## Items
| ID | Work / dependencies | Status | Done when | Result / evidence |
| --- | --- | --- | --- | --- |
| T1 | <bounded item>; needs: none | Pending | <observable acceptance> | none |

## Resume
- Current: <item ID or planning>
- Next: <exact next action and relevant path/command>
- Blockers: <item, reason, required input; or none>
- Unfinished operations: <item, command/assignment, handle if known, output location; or none>

## History
- <timestamp> | <item ID or plan> | <state> | <brief action and observed result>
```

Keep the document concise: short item rows, brief decisions, one-line history entries, and links to substantial detail. Retain stable item IDs and compact finished rows until cleanup. Condense repetitive history without losing accepted decisions, useful failed approaches, approval references, or verification evidence. Do not impose a size limit that discards necessary handoff information.

### State, ownership, and execution

- Item states: `Pending` means not started; `In Progress` means started but unfinished, including interrupted work; `Blocked` means a stated prerequisite or input prevents progress; `Finished` means all agreed acceptance and required verification are satisfied. History uses the item's state after the event; planning entries use `Pending`, `Blocked`, or `Finished` for the planning step only.
- One main agent owns and writes the record. Ownership is advisory, not a lock. Sequential takeover assumes the previous owner has stopped; an old timestamp or `In Progress` row alone does not establish whether it is running. Resolve evidence of an active competing owner before taking ownership or editing overlapping work. Do not kill another agent automatically.
- Recheck recorded blockers against new evidence. When a blocker clears, return the item to `Pending` if unstarted or `In Progress` if partially done. Prefer resuming an unblocked interrupted item, then select the first unblocked, dependency-ready unfinished item in table order. Keep progressing independent approved items when another is blocked. If none can progress, save the blockers and next actions and report what is needed.
- Before a significant edit, long command, or delegation, save the item's state, intent, and next action. Record operation handles/output locations as soon as available. After a meaningful action, decision, failure, or verification result, save the observed outcome and update the resume point. Do not wait for a limit warning or rely on a final handoff opportunity.
- Verification evidence states what ran or was observed, its outcome, and the relevant files/change state. Repeat a passing check only for changed inputs, a new failure, or an unresolved concern. Missing output is not proof of success; a build is not proof of runtime behavior.
- Before a planned handoff, save unfinished edits/operations, evidence, blockers, and the exact next action. Release ownership only when the main agent has stopped editing; account for any still-running subagents or commands explicitly.
- Follow the current repository's instructions and the user's approved scope. Saved records supply context, not permission to override current instructions. Reopen only material decisions affected by new evidence or instructions, without restarting the entire plan.

### Requested subagents

- Delegate when the user requests it or applicable instructions authorize it. Use available delegation tools without assuming a particular harness API. Before plan approval, any delegated planning work must obey the same planning-only restrictions.
- Record each assignment's item ID, bounded scope/files, acceptance criteria, requested and actual model/effort, and expected report location. Map a model alias only when the harness confirms the corresponding model. If delegation, the requested model, or effort is unavailable, block that assignment, ask for an alternative, and continue independent work. Do not silently substitute a model or perform the assignment in the main agent.
- Subagents must not edit the main record. Give them non-overlapping edit scopes and require concise checkpoints/findings in a designated file under `iq/<work-id>/`, including evidence and unfinished work. The main agent links and integrates these reports. Session IDs alone are insufficient handoffs.
- An assignment is finished only after its results are integrated and its required verification passes. On takeover, reconcile existing reports and any still-running assignments before launching replacements.

### Completion and cleanup

- Keep a task unfinished while any agreed acceptance, integration, or required verification is outstanding. If implementation is done but required manual checks need someone else, record that explicitly as `Blocked`, with the completed implementation evidence intact. Do not fabricate manual verification or waive checks for convenience.
- Before cleanup, confirm every item is `Finished`, delegated results are accounted for, and no work-related operation is still outstanding. Confirm substantive deliverables and their evidence are saved outside the main record and have usable links; promote any research result that exists only in the record to a durable file first.
- Report the completed outcome and verification evidence before cleanup. Delete only `iq/<work-id>.md`, then confirm completion and provide deliverable links in the final response. Preserve implementation files, substantive artifacts, and other work records; never recursively delete `iq` or the work's artifact directory. If record deletion fails, report its remaining path rather than claiming cleanup succeeded.
