#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Mail.Controls.Core.VoiceBubble;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS;

/// <summary>
/// Bridge commands for the voice bubble. "voice-status" reports the panel, the shortcut and what gates it.
/// "voice-hotkey" presses the voice shortcut, which runs the real flow and opens the microphone.
/// "voice-demo &lt;state&gt;" shows a state with sample content without the microphone or the API:
/// listening, analyzing, inline, steps, question, success, failed, hide.
/// </summary>
public sealed partial class AppDelegate
{
    private VoiceBubbleSimulator? _voiceSimulator;

    private void RegisterVoiceDebugCommands()
    {
        MacDebugBridge.Register("voice-status", async _ =>
        {
            var result = string.Empty;
            var entitlement = await _services!.GetRequiredService<IWinoAccountIntelligenceSnapshotService>().GetEntitlementAsync();
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                var preferences = _services!.GetRequiredService<IPreferencesService>();
                result = _voiceHost is null
                    ? "no voice host"
                    : $"{_voiceHost.DescribePanel()} {_voiceHost.DescribeHotKey()} enabled={preferences.IsVoiceControlEnabled} " +
                      $"hotkeyEnabled={preferences.IsVoiceHotKeyEnabled} gesture={preferences.VoiceHotKeyModifiers}+{preferences.VoiceHotKeyKey} " +
                      $"position={preferences.VoiceBubblePosition} intelligence={entitlement.CanAccessSurfaces} menuBarIcon={_statusItem?.ButtonScreenFrame?.ToString() ?? "none"}";
            });
            return result;
        });

        MacDebugBridge.Register("voice-hotkey", async _ =>
        {
            await _dispatcher.ExecuteOnUIThread(() => _voiceHost?.SimulateHotKey());
            return "pressed";
        });

        MacDebugBridge.Register("voice-demo", async args =>
        {
            var state = args.Length > 0 ? args[0].ToLowerInvariant() : "listening";
            await _dispatcher.ExecuteOnUIThread(() => ShowVoiceDemo(state));
            await Task.Delay(TimeSpan.FromSeconds(1));
            return _voiceHost?.DescribePanel() ?? "no voice host";
        });
    }

    private void ShowVoiceDemo(string state)
    {
        if (_voiceHost is not { } host)
            return;

        var bubble = host.Bubble;
        var preferences = _services!.GetRequiredService<IPreferencesService>();
        StopVoiceDemoFrames();

        if (state == "hide")
        {
            bubble.Present(VoiceBubbleState.Hidden);
            return;
        }

        if (bubble.State == VoiceBubbleState.Hidden)
            host.Show(preferences.VoiceBubblePosition);

        var outlook = new AccountIconInfo(AccountIconProvider.Microsoft, null, "#0F6CBD");
        var gmail = new AccountIconInfo(AccountIconProvider.Google, null, "#16A085");

        switch (state)
        {
            case "listening":
                bubble.Transcript = null;
                bubble.InterimTranscript = null;
                bubble.Present(VoiceBubbleState.Listening);
                _voiceSimulator = new VoiceBubbleSimulator();
                _voiceSimulator.Speak("Archive every newsletter from last week and flag the mail from Burak about the release");
                host.FrameRendered += VoiceDemoFrame;
                host.StartFrames();
                break;
            case "analyzing":
                bubble.Transcript = "Archive every newsletter from last week and flag the mail from Burak about the release";
                bubble.InterimTranscript = null;
                bubble.Present(VoiceBubbleState.Analyzing);
                break;
            case "inline":
                bubble.ConfirmationGroups = null;
                bubble.ConfirmationSteps = null;
                bubble.ConfirmationText = VoiceBubbleText.FromMarked("Archive **12 newsletters** from last week?");
                bubble.ConfirmText = "Yes";
                bubble.DeclineText = "No";
                bubble.Present(VoiceBubbleState.Confirmation);
                break;
            case "steps":
                bubble.ConfirmationText = null;
                bubble.ConfirmationSteps = null;
                bubble.ConfirmText = "Yes";
                bubble.DeclineText = "No";
                bubble.ConfirmationGroups =
                [
                    new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.Account, "Work", "burak@contoso.com", [outlook],
                    [
                        new VoiceBubbleStep(VoiceBubbleText.FromMarked("Archive **12 mails** from newsletters"), "Received last week, in Inbox"),
                        new VoiceBubbleStep(VoiceBubbleText.FromMarked("Flag the mail from **Burak** about the release"))
                    ]),
                    new VoiceBubbleStepGroup(VoiceBubbleStepGroupKind.Accounts, "Personal, Family", "2 accounts", [gmail, outlook],
                    [
                        new VoiceBubbleStep(VoiceBubbleText.FromMarked("Archive **3 mails** from newsletters"))
                    ],
                    ["The Family account has no Archive folder."])
                ];
                bubble.Present(VoiceBubbleState.Confirmation);
                break;
            case "question":
                bubble.QuestionText = "Which account is \"work\"?";
                bubble.QuestionCaption = "Wino remembers your answer.";
                bubble.IsQuestionSearchable = true;
                bubble.ClearQuestionFilter();
                bubble.SetChoices(
                [
                    new VoiceBubbleChoice("h:1", "burak@contoso.com", null, outlook, isHeader: true),
                    new VoiceBubbleChoice("1", "Inbox", "1,204 mails", null),
                    new VoiceBubbleChoice("2", "Projects", "Inbox / Projects", null),
                    new VoiceBubbleChoice("h:3", "burak@gmail.com", null, gmail, isHeader: true),
                    new VoiceBubbleChoice("3", "Inbox", null, null),
                    new VoiceBubbleChoice("4", "Receipts", "Archive / Receipts", null)
                ]);
                bubble.Present(VoiceBubbleState.Question);
                break;
            case "success":
                bubble.SuccessDismissMilliseconds = 6000;
                bubble.Message = VoiceBubbleText.FromMarked("Archived **12 mails**.\nFlagged **1 mail**.");
                bubble.Present(VoiceBubbleState.Success);
                break;
            case "failed":
                bubble.CanRetry = true;
                bubble.Message = VoiceBubbleText.FromMarked("Wino couldn't reach the voice service. Check your connection and try again.");
                bubble.Present(VoiceBubbleState.Failed);
                break;
        }
    }

    private void VoiceDemoFrame(object? sender, double elapsed)
    {
        if (_voiceSimulator is not { } simulator || _voiceHost is not { } host)
            return;

        simulator.Step(Math.Clamp(elapsed, 0, 0.05));
        host.Bubble.UpdateBands(simulator.Bands);
        host.Bubble.Transcript = simulator.HeardText;
        host.Bubble.InterimTranscript = simulator.InterimText;
        if (simulator.IsFinished && simulator.Silence > 1.5)
            StopVoiceDemoFrames();
    }

    private void StopVoiceDemoFrames()
    {
        if (_voiceHost is { } host)
        {
            host.FrameRendered -= VoiceDemoFrame;
            host.StopFrames();
        }

        _voiceSimulator = null;
    }
}
#endif
