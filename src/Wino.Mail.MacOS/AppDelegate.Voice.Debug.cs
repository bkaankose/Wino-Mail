#if DEBUG
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Services.Assistant;
using Wino.Agent.Runtime.Ports;
using Wino.Core.Domain.Enums;
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
        // assistant-secret new|enter: the memory sync password sheet with its wording; nothing is kept.
        MacDebugBridge.Register("assistant-secret", async args =>
        {
            var isNew = args.Length == 0 || args[0] != "enter";
            var request = new SyncSnapshotSecretRequest(true, false, isNew,
                isNew ? Translator.VoiceBrain_Sync_NewPassword_Title : Translator.VoiceBrain_Sync_Password_Title,
                isNew ? Translator.VoiceBrain_Sync_NewPassword_Description : Translator.VoiceBrain_Sync_Password_Description,
                Translator.VoiceBrain_Sync_PasswordLabel, Translator.VoiceBrain_Sync_ConfirmPasswordLabel);
            _ = _services!.GetRequiredService<IMailDialogService>().ShowWinoAccountSyncSecretDialogAsync(request);
            await Task.Delay(400);
            return "shown";
        });

        // assistant-view calendar|tasks|contacts|settings|mailRules: the page an OpenView step opens.
        MacDebugBridge.Register("assistant-view", async args =>
        {
            if (args.Length == 0 || !Enum.TryParse<AgentView>(args[0], ignoreCase: true, out var view)) return "usage: assistant-view <view>";
            await OpenVoiceViewAsync(view, null);
            return "ok " + view;
        });

        // assistant-rule-run <account address>: runs a CreateMailRule step the way a confirmed plan does (mark read and
        // archive every mail whose subject contains "wino-debug-rule"), which creates two Wino rules and opens the first.
        // assistant-rule-clean <account address> deletes the rules it made.
        MacDebugBridge.Register("assistant-rule-run", async args =>
        {
            var account = (await _services!.GetRequiredService<IAccountService>().GetAccountsAsync()).FirstOrDefault(a => args.Length > 0 && string.Equals(a.Address, args[0], StringComparison.OrdinalIgnoreCase));
            if (account is null) return "no such account";
            var rule = new StepArgs.MailRuleArgs(account.Id, "wino-debug-rule", MailFilterMatchMode.All,
                [new MailRuleCondition(MailFilterConditionField.Subject, MailFilterConditionOperator.Contains, "wino-debug-rule")],
                [new MailRuleAction(MailFilterActionType.MarkRead), new MailRuleAction(MailFilterActionType.Archive)]);
            var step = new ResolvedStep(1, AgentAction.CreateMailRule, StepTarget.None.Instance, rule, null, null, RiskLevel.Medium, true, "debug rule", null);
            var executor = _services!.GetRequiredService<AgentPlanExecutor>();
            executor.Host = new MacVoicePlanHost(this);
            var outcome = await executor.ExecuteAsync(new ResolvedPlan(Guid.NewGuid(), [step], "debug rule", 1, false), new Progress<StepOutcome>(), CancellationToken.None);
            var filters = await _services!.GetRequiredService<IMailFilterService>().GetFiltersAsync(account.Id) ?? [];
            var made = filters.Where(f => f.Name.StartsWith("wino-debug-rule", StringComparison.Ordinal))
                .Select(f => $"{f.Name}: {string.Join(",", f.Actions.Select(a => a.Type))} target={(f.Actions.Any(a => a.TargetRemoteFolderId is not null) ? "set" : "none")} source={(f.SourceRemoteFolderId is null ? "none" : "inbox")}");
            return $"{outcome.Steps[0].Status} '{outcome.Steps[0].Detail}' | {string.Join(" | ", made)}";
        });
        MacDebugBridge.Register("assistant-rule-clean", async args =>
        {
            var account = (await _services!.GetRequiredService<IAccountService>().GetAccountsAsync()).FirstOrDefault(a => args.Length > 0 && string.Equals(a.Address, args[0], StringComparison.OrdinalIgnoreCase));
            if (account is null) return "no such account";
            var service = _services!.GetRequiredService<IMailFilterService>();
            var mine = (await service.GetFiltersAsync(account.Id) ?? []).Where(f => f.Name.StartsWith("wino-debug-rule", StringComparison.Ordinal)).ToList();
            foreach (var f in mine) await service.DeleteFilterAsync(f.Id);
            return $"deleted {mine.Count}";
        });

        // assistant-rule: the rule editor as a created rule opens it (the first account's first rule; navigation only).
        MacDebugBridge.Register("assistant-rule", async _ =>
        {
            var accounts = await _services!.GetRequiredService<IAccountService>().GetAccountsAsync();
            foreach (var account in accounts)
            {
                var filters = await _services!.GetRequiredService<IMailFilterService>().GetFiltersAsync(account.Id) ?? [];
                if (filters.Count == 0) continue;
                await OpenVoiceMailRuleAsync(account.Id, filters[0].Id);
                return $"ok {filters[0].Name}";
            }

            return "no rules on any account";
        });

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
