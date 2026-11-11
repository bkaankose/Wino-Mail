#if DEBUG
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Agent.Runtime;
using Wino.Agent.Runtime.Clarify;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Session;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services.Assistant;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Services.Assistant;

namespace Wino.Mail.MacOS;

/// <summary>
/// Bridge commands for the local lab (a Debug build started with WINO_API_URL pointing at a local API):
/// "lab-signin EMAIL" signs in with the password in {tmp}/wino-debug/lab-password, then deletes that file.
/// "assistant-ask TEXT" sends a typed request through the real runtime and prints the turn (plan, question or message).
/// "assistant-pick N" answers the pending question with option N (1-based). "assistant-run" confirms and runs the
/// last plan through the real executor and prints each step's outcome. Nothing here logs the password.
/// </summary>
public sealed partial class AppDelegate
{
    private IAgentSession? _labSession;
    private ResolvedPlan? _labPlan;

    private void RegisterLabDebugCommands()
    {
        MacDebugBridge.Register("lab-signin", async args =>
        {
            if (args.Length == 0) return "usage: lab-signin EMAIL";
            var file = Path.Combine(Path.GetTempPath(), "wino-debug", "lab-password");
            if (!File.Exists(file)) return "no password file";
            var password = (await File.ReadAllTextAsync(file)).Trim();
            File.Delete(file);
            var result = await _services!.GetRequiredService<IWinoAccountProfileService>().LoginAsync(args[0], password);
            return result.IsSuccess ? "signed in" : "failed: " + result.ErrorCode;
        });

        MacDebugBridge.Register("assistant-ask", async args =>
        {
            if (args.Length == 0) return "usage: assistant-ask TEXT";
            var host = _services!.GetRequiredService<IAgentRuntimeHost>();
            await host.EnsureInitializedAsync();
            _labSession ??= host.Runtime.OpenSession();
            var turn = await _labSession.HandleAsync(new AgentRequest(string.Join(' ', args)), CancellationToken.None);
            return DescribeTurn(turn);
        });

        MacDebugBridge.Register("assistant-pick", async args =>
        {
            if (_labSession?.PendingClarification is not { } question) return "no question";
            if (args.Length == 0 || !int.TryParse(args[0], out var n) || n < 1 || n > question.Options.Count) return "usage: assistant-pick N";
            var turn = await _labSession.AnswerAsync(ClarificationAnswer.Pick(question.Id, question.Options[n - 1].Key), CancellationToken.None);
            return DescribeTurn(turn);
        });

        MacDebugBridge.Register("assistant-run", async _ =>
        {
            if (_labSession is null || _labPlan is null) return "no plan";
            var plan = _labPlan;
            var turn = await _labSession.ConfirmAsync(new PlanConfirmation(plan.Id, plan.Steps.Select(s => s.N).ToList()), CancellationToken.None);
            if (turn is not AgentTurn.Plan { Resolved: var confirmed }) return "confirm: " + DescribeTurn(turn);
            var executor = _services!.GetRequiredService<AgentPlanExecutor>();
            executor.Host = new MacVoicePlanHost(this);
            var outcome = await executor.ExecuteAsync(confirmed, new Progress<StepOutcome>(), CancellationToken.None);
            await _labSession.ReportAsync(outcome, CancellationToken.None);
            _labPlan = null;
            return string.Join(" | ", outcome.Steps.Select(s => $"{s.N}:{s.Status} {s.Detail} ({s.Touched.Count})"));
        });

        // bubble-type [sel=N,M] TEXT: types TEXT into the real assistant bubble and sends it, about the mails at list rows
        // N,M of the current mail list when given. bubble-run presses the plan's Run button.
        MacDebugBridge.Register("bubble-type", async args =>
        {
            if (_voiceController is null) return "no voice controller";
            Wino.Core.Domain.Models.Voice.VoiceLocalSelection? selection = null;
            var words = args.ToList();
            if (words.Count > 0 && words[0].StartsWith("sel=", StringComparison.Ordinal))
            {
                var rows = words[0][4..].Split(',').Select(int.Parse).ToList();
                words.RemoveAt(0);
                var mails = await _services!.GetRequiredService<IMailService>().FetchMailsAsync(new Wino.Core.Domain.Models.MailItem.MailListInitializationOptions(
                    [await LabInboxAsync()], FilterOptionType.All, SortingOptionType.ReceiveDate, false, null, null), CancellationToken.None);
                var picked = rows.Where(r => r < mails.Count).Select(r => mails[r]).ToList();
                if (picked.Count == 0) return "no such rows";
                selection = Wino.Core.Domain.Models.Voice.VoiceLocalSelection.ForMails(picked);
            }

            await _dispatcher.ExecuteOnUIThread(() => _voiceController.DebugSendTyped(string.Join(' ', words), selection));
            return selection is null ? "sent" : $"sent about {selection.Count}: " + string.Join(" / ", selection.Mails.Select(m => m.Subject));
        });

        MacDebugBridge.Register("bubble-run", async _ =>
        {
            if (_voiceController is null) return "no voice controller";
            await _dispatcher.ExecuteOnUIThread(() => _voiceController.DebugConfirm());
            return "confirmed";
        });

        // lab-sync: a full calendar and contact sync of every account, as after adding one.
        MacDebugBridge.Register("lab-sync", async _ =>
        {
            foreach (var account in await _services!.GetRequiredService<IAccountService>().GetAccountsAsync())
            {
                WeakReferenceMessenger.Default.Send(new Wino.Messaging.Server.NewCalendarSynchronizationRequested(
                    new Wino.Core.Domain.Models.Synchronization.CalendarSynchronizationOptions { AccountId = account.Id, Type = CalendarSynchronizationType.Strict }));
                WeakReferenceMessenger.Default.Send(new Wino.Messaging.Server.NewContactSynchronizationRequested(
                    new Wino.Core.Domain.Models.Synchronization.ContactSynchronizationOptions { AccountId = account.Id, Type = ContactSynchronizationType.Full }));
            }

            return "requested";
        });

        MacDebugBridge.Register("assistant-reset", _ =>
        {
            _labSession?.Reset();
            _labSession = null;
            _labPlan = null;
            return Task.FromResult("reset");
        });
    }

    private async Task<Wino.Core.Domain.Entities.Mail.MailItemFolder> LabInboxAsync()
    {
        var account = (await _services!.GetRequiredService<IAccountService>().GetAccountsAsync()).First();
        return await _services!.GetRequiredService<IFolderService>().GetSpecialFolderByAccountIdAsync(account.Id, Wino.Core.Domain.Enums.SpecialFolderType.Inbox);
    }

    private string DescribeTurn(AgentTurn turn)
    {
        switch (turn)
        {
            case AgentTurn.Plan { Resolved: var plan }:
                _labPlan = plan;
                return "PLAN " + string.Join(" ; ", plan.Steps.Select(s => $"{s.N}:{s.Action} {s.Target.GetType().Name}({s.EstimatedCount?.ToString() ?? "?"}) {s.Args.GetType().Name} risk={s.Risk} confirm={s.NeedsConfirmation} '{s.Gloss}'"));
            case AgentTurn.Clarify { Clarification: var q }:
                return $"ASK {q.Kind} '{q.SaidAs}': " + string.Join(", ", q.Options.Take(8).Select((o, i) => $"{i + 1}={o.Title}"));
            case AgentTurn.Message { Kind: var kind, Gloss: var gloss }:
                return $"MESSAGE {kind} '{gloss}'";
            default:
                return turn.GetType().Name;
        }
    }
}
#endif
