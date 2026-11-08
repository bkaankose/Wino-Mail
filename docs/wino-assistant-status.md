# Wino Assistant: work split and status

Coordination file for the sessions building [the plan](wino-assistant-plan.md) on `feature/wino-assistant`. Both sessions pull (rebase) before pushing and update their own section here in the same commit as their work. The Mac session cannot message the Windows session: anything it needs from the shared layer goes under "Requests to the shared layer" below.

## Ownership

| Area | Owner |
|---|---|
| Wino.Mail.Contracts, Wino-Mail-API (planner, mapping, quota, transcription audit) | Windows session |
| Shared client layer: `Wino.Core.Domain`, `Wino.Services`, `Wino.Core` (executor), `Wino.Mail.ViewModels` (`VoiceBubbleController`), `controls/Wino.Mail.Controls.Core`, translations | Windows session |
| Windows head, `Wino.Platform.Windows`, `controls/Wino.Mail.Controls.WinUI` | Windows session |
| macOS head, `Wino.Platform.MacOS`, `Wino.Platform.Apple`, AppKit controls | Mac session |
| iOS, Android | later |
| Voice runner and corpus (`tools/Wino.Intelligence.Console`) | Windows session |

The Mac session does not edit shared projects. If it finds assistant logic in a platform head (matching, wording, filtering, fallbacks), it lists it under requests so it moves to the shared layer for every platform.

## Phase 1: Wino Assistant bubble (in progress)

Goal: the voice bubble becomes Wino Assistant with a text mode (plan section 2) and the new quota (section 3).

### Shared view contract (both platforms implement it)

`controls/Wino.Mail.Controls.Core/VoiceBubble/IVoiceBubbleView.cs` gains (names final once the Windows commit lands; the Mac side builds against this list):

- `VoiceBubbleInputMode InputMode { get; set; }`: `Voice` or `Text`. Set by the controller before `Present`.
- `VoiceBubbleState.Composing`: new state. Text mode shows an editable, styled text surface with a caret where the transcript appears in voice mode, with the same rising text animation as transcription. Not a plain text box.
- `string ComposedText { get; set; }`: the editable text. The controller sets it (for example when spoken words are appended); the view raises `ComposedTextChanged` when the user edits it.
- `string ComposePlaceholder { get; set; }`
- `bool CanSwitchToVoice { get; set; }`: shows the microphone button in `Composing`. False when the voice quota is used up or no microphone is available.
- `string? InputNotice { get; set; }`: one short line under the text, for example that voice requests are used up this month.
- `event EventHandler<string>? ComposedTextChanged;`
- `event EventHandler? SwitchToVoiceRequested;`: the microphone button. The controller starts listening and appends transcribed words to `ComposedText` as they arrive (interim words shown after the typed text), then sends as a voice request.
- `SendRequested` (existing) also fires on Enter in `Composing`; Shift+Enter inserts a line break.

### Controller and settings (shared, Windows session)

- `VoiceBubbleController.StartAsync(VoiceBubbleInputMode mode)`. Voice falls back to Text automatically when the voice quota is used up, with `InputNotice` set.
- Text requests never open the transcription provider (start frame says `text`); a voice request that began as typed text sends the typed part with the audio and the server plans typed + spoken text.
- Preferences: the existing voice shortcut stays; a new text shortcut preference. Each platform registers both through its existing hot-key path and shows both on its Voice Control (to be: Assistant) settings page.
- User-facing name "Wino Assistant" in translations. Type and namespace renames (`VoiceBubble*` to `AssistantBubble*`) happen in one mechanical commit by the Windows session, announced here; the Mac session then fixes the Mac build.

### Mac session tasks

1. Build the AppKit side of the view contract: `Composing` state with a styled editable text surface (caret, same typography and rising animation as the transcript), placeholder, microphone switch button, `InputNotice` line, Enter to send. Keep it behind the contract; no logic beyond forwarding events.
2. Register the text shortcut (new preference) next to the voice one and add it to the Mac Voice Control settings page.
3. Show "Wino Assistant" where the Mac UI names the feature (strings come from translations).
4. Audit the Mac voice code (`src/Wino.Mail.MacOS/Infrastructure/Voice`, `AppDelegate.Voice.cs`, settings pages) against plan section 0 and list any logic that belongs in the shared layer under requests.
5. Build the Mac app (Debug, arm64) after each pull and record the result below.

## Status

### Windows session
- 8 Nov: branch created from `7b3add0d` (Mac voice port merged with the Windows work); plan and this file added. Next: phase 1 shared contract, controller text mode, contracts and API changes.

### Mac session
- (not started)

## Requests to the shared layer
- (none yet)
