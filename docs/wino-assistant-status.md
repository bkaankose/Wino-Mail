# Wino Assistant: status

Progress of [the plan](wino-assistant-plan.md) on `feature/wino-assistant`.

## How the work runs

- One session on the Windows PC writes all code: the contracts and API, the shared client layer, and the Windows and macOS heads.
- The Mac (`burak-mac`) is only the build machine for the macOS head. It pulls this branch into `~/Desktop/Wino-Mail-assistant` and builds Debug arm64 there (ad hoc signing over SSH). No code is written on the Mac.
- iOS and Android follow later, on the same shared layer.

## Phase 1: Wino Assistant bubble (done on Windows, 8 Nov)

Goal: the voice bubble becomes Wino Assistant with a text mode (plan section 2) and the new quota (section 3).

### Shared view contract

`controls/Wino.Mail.Controls.Core/VoiceBubble/IVoiceBubbleView.cs` gains:

- `VoiceBubbleInputMode InputMode { get; set; }`: `Voice` or `Text`. Set by the controller before `Present`.
- `VoiceBubbleState.Composing`: text mode shows an editable, styled text surface with a caret where the transcript appears in voice mode, with the same rising text animation. Not a plain text box.
- `string ComposedText { get; set; }`: the editable text. The controller sets it (for example when spoken words are appended); the view raises `ComposedTextChanged` when the user edits it.
- `string ComposePlaceholder { get; set; }`
- `bool CanSwitchToVoice { get; set; }`: shows the microphone button in `Composing`. False when the voice quota is used up or no microphone is available.
- `string? InputNotice { get; set; }`: one short line under the text, for example that voice requests are used up this month.
- `event EventHandler<string>? ComposedTextChanged;`
- `event EventHandler? SwitchToVoiceRequested;`: the microphone button. The controller starts listening and appends transcribed words to `ComposedText` as they arrive, then sends as a voice request.
- `SendRequested` also fires on Enter in `Composing`; Shift+Enter inserts a line break.

### Controller and settings (shared)

- `VoiceBubbleController.StartAsync(VoiceBubbleInputMode mode)`. Voice falls back to Text automatically when the voice quota is used up, with `InputNotice` set.
- Text requests never open the transcription provider (the start frame says `text`); a voice request that began as typed text sends the typed part with the audio, and the server plans the typed and spoken text together.
- Preferences: the existing voice shortcut stays; a new text shortcut preference. Each platform registers both through its existing hot-key path and shows both on its settings page.
- User-facing name "Wino Assistant" in translations; type and namespace renames (`VoiceBubble*` to `AssistantBubble*`) in one mechanical commit across all heads.

### macOS head (written here, built on burak-mac)

- AppKit `Composing` state in the Mac bubble view, the text shortcut and its settings row, "Wino Assistant" naming.
- Audit of the Mac voice code against plan section 0; logic that belongs in the shared layer moves there.

## Phase 2: brain, learned facts and device intents (Windows, 8 Nov)

Plan sections 4 and the brain part of 7. Contracts `3.0.0-alpha.20`.

- Brain sections carry the account's name and order; events are gone from the contract, the renderer and the client. Accounts render in the user's order (`a1` is the first account) and the account name names the account. Top contacts leave out every own address and alias. Renaming or reordering accounts pushes, and only when the section's value changed.
- New learned facts: sender alias (a domain the user picked, used for mail selections), group (people picked one by one, then Done), default account for new mails and tasks (asked by the server when several accounts could take it), filter word (a word for a kind of sender). "What Wino remembers" lists them.
- Device intents in both modes: sender kind on mail selections (classified on the device from the sender's address and name), free-slot requests on new and moved events (the device picks the gap across every calendar, inside working hours or a part of the day), and the week start and working hours in each request.

## Phase 3: action catalog (8 Nov)

Plan section 6 and the command part of 7. Contracts `3.0.0-alpha.21`.

- 43 new actions across mail (move, junk, Focused/Other, pin, categories, send drafts, translate, analyze, copy code, unsubscribe, reply all and forward through the draft kind, undo, sync), events (reminder, join, series scope), tasks (find, complete, reopen, update, step, delete, lists), contacts (find, favorite, lists, category, create, delete) and folders (mark read, empty, pin, rename, create, delete, sync).
- Up to five commands; chains within a kind through `usesResultOf`, across kinds through `@result.N` person tokens.
- The planner's grammar can't take more properties, so the details ride in the existing `events` JSON string as nested objects. The mapper accepts the shapes the planner actually writes (a nested object as a string, a mail change at the top level, a step through UpdateTask, a forward on another mail action).
- Server mapping covered by unit tests; the client executor for the new actions is the remaining half.

## Phase 4: context-aware requests, server side (8 Nov)

Plan section 5. The interactions in the app are still deferred; only a debug entry point is planned for testing.

- A start frame with a selection (kind, count, known flags, people as address hashes) is planned with its own prompt variant (the private prompt plus the selection rules) and the private schema, in both modes.
- People of the selection are named to the planner only as brain aliases (Personal mode, known people); everyone else is "unknown". No subjects, bodies, titles or names leave the device.
- A command on the selection's kind acts on the selection (`UsesContext`) whatever selection the planner wrote; actions outside the kind's allowed set (plus creating something new) become NotActionable.

## Log

- 8 Nov: branch created from `7b3add0d` (macOS voice port merged with the Windows work); plan and this file added. A headless agent briefly started on the Mac was stopped before making changes; all work now runs from the Windows session.
- 8 Nov: API 45d73e3 deployed (typed requests, assistant 200 and voice 50 allowances, 1-hour prompt cache, planner accuracy fixes). Phase 1 checked in the Windows Debug app on production: typing shortcut opens the bubble focused, typed text uses the transcript style and scrolls past two lines, Enter sends a typed request (assistant allowance only, no transcription), the microphone switch keeps the typed text and returns to typing when nothing is said, cancelled voice attempts are refunded. The macOS head compiles against the new contract (built on burak-mac); further macOS work is paused.

