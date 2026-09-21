using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MimeKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Rules;
using Wino.Core.Rules;

namespace Wino.Dialogs.Rules;

/// <summary>
/// Orchestrates the inbox-rule dialogs for one account: the manager (which edits rules in place), the
/// stand-alone editor used by "Create rule from message", the classic-Outlook rule-blob consent retry,
/// and the client-side "Run rules now". WinUI allows only one open ContentDialog, so every dialog here
/// is shown through the dialog service one at a time; the manager hides itself and hands off with a
/// flag when run-now needs its own dialogs, and is re-opened afterwards.
/// </summary>
public sealed class InboxRulesCoordinator
{
    private readonly IServiceProvider _services;
    private readonly IMailDialogService _dialogService;
    private readonly Func<ContentDialog, Task<ContentDialogResult>> _present;
    private readonly ElementTheme _theme;

    public InboxRulesCoordinator(IServiceProvider services,
                                 IMailDialogService dialogService,
                                 Func<ContentDialog, Task<ContentDialogResult>> present,
                                 ElementTheme theme)
    {
        _services = services;
        _dialogService = dialogService;
        _present = present;
        _theme = theme;
    }

    private sealed class RulesContext
    {
        public required MailAccount Account { get; init; }
        public required IRuleService RuleService { get; init; }
        public required IReadOnlyList<MailItemFolder> Folders { get; init; }
        public required IReadOnlyList<MailCategory> Categories { get; init; }
    }

    /// <summary>Opens the Rules &amp; Alerts manager for the account.</summary>
    public async Task ShowManagerAsync(MailAccount account)
    {
        try
        {
            var context = await TryBuildContextAsync(account);
            if (context == null)
                return;

            await RunManagerLoopAsync(context);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Rules dialog flow failed.");
            await ShowInfoAsync(string.Format(Translator.Rules_OperationFailed, ex.Message));
        }
    }

    /// <summary>Opens the rule editor for a new rule seeded with a From = sender condition.</summary>
    public async Task ShowEditorForSenderAsync(MailAccount account, string? senderAddress)
    {
        try
        {
            var context = await TryBuildContextAsync(account);
            if (context == null)
                return;

            var rule = new RemoteInboxRule
            {
                Name = string.Empty,
                IsEnabled = true,
            };

            if (!string.IsNullOrWhiteSpace(senderAddress))
                rule.Conditions.Add(new RuleConditionModel(RuleConditionField.From, senderAddress));

            await ShowEditorAsync(context, rule);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Create rule from mail failed.");
            await ShowInfoAsync(string.Format(Translator.Rules_OperationFailed, ex.Message));
        }
    }

    private async Task<RulesContext?> TryBuildContextAsync(MailAccount? account)
    {
        var ruleService = _services.GetService<IRuleService>();
        if (account == null || ruleService == null)
            return null;

        if (!ruleService.SupportsRules(account))
        {
            await ShowInfoAsync(Translator.Rules_ExchangeOnly, Translator.Rules_Title);
            return null;
        }

        var folderService = _services.GetRequiredService<IFolderService>();
        var categoryService = _services.GetRequiredService<IMailCategoryService>();

        var folders = (await folderService.GetFoldersAsync(account.Id))?
            .Where(f => f.IsMoveTarget).ToList() ?? new List<MailItemFolder>();
        var categories = await categoryService.GetCategoriesAsync(account.Id) ?? new List<MailCategory>();

        return new RulesContext
        {
            Account = account,
            RuleService = ruleService,
            Folders = folders,
            Categories = categories
        };
    }

    // The manager edits rules in place (it hosts the editor view). Run-now needs its own dialogs and
    // ContentDialogs can't stack, so for that one hand-off the manager closes and re-opens afterwards.
    private async Task RunManagerLoopAsync(RulesContext context)
    {
        bool reopenAfterRunNow;
        do
        {
            var manager = new RulesManagerDialog(context.Account, context.RuleService, context.Folders, context.Categories)
            {
                RequestedTheme = _theme
            };
            await _present(manager);

            reopenAfterRunNow = manager.RunNowRequested;
            if (reopenAfterRunNow)
                await RunRulesNowAsync(context);
        }
        while (reopenAfterRunNow);
    }

    private async Task ShowEditorAsync(RulesContext context, RemoteInboxRule? rule)
    {
        var editor = new RuleEditorDialog(rule, context.Folders, context.Categories) { RequestedTheme = _theme };
        await _present(editor);

        if (editor.DeleteRequested && rule?.Id != null)
        {
            await ApplyMutationWithBlobConsentAsync(
                removeBlob => context.RuleService.DeleteRuleAsync(context.Account.Id, rule.Id, removeBlob));
        }
        else if (editor.Saved && editor.ResultRule != null)
        {
            var result = editor.ResultRule;

            // A new rule appends to the end of the priority order.
            if (string.IsNullOrEmpty(result.Id))
            {
                var existing = await context.RuleService.GetRulesAsync(context.Account.Id);
                result.Priority = (existing.Count == 0 ? 0 : existing.Max(r => r.Priority)) + 1;
            }

            await ApplyMutationWithBlobConsentAsync(
                removeBlob => context.RuleService.SaveRuleAsync(context.Account.Id, result, removeBlob));
        }
    }

    // Runs a rule mutation from the editor hand-off; when the server reports the classic-Outlook rule
    // blob blocks the update, asks for consent and retries with the blob removed. Other failures are
    // surfaced in a plain info dialog (the editor has already closed, so this is the only feedback path).
    private async Task ApplyMutationWithBlobConsentAsync(Func<bool, Task<InboxRuleUpdateResult>> operation)
    {
        var result = await operation(false);

        if (result.RequiresOutlookRuleBlobRemoval)
        {
            var consent = await _dialogService.ShowConfirmationDialogAsync(
                Translator.Rules_RemoveOutlookBlob_Body,
                Translator.Rules_RemoveOutlookBlob_Title,
                Translator.Rules_RemoveOutlookBlob_Button);

            if (!consent)
                return;

            result = await operation(true);
        }

        if (!result.Success)
            await ShowInfoAsync(FormatSaveFailure(result));
    }

    /// <summary>The user-facing message for a rule change the server refused.</summary>
    internal static string FormatSaveFailure(InboxRuleUpdateResult result)
    {
        var detail = result.Errors.Count > 0 ? string.Join(" ", result.Errors) : "unknown error";
        return string.Format(Translator.Rules_SaveFailed, detail);
    }

    // Client-side "Run rules now" over the Inbox: evaluate enabled rules and apply the executable
    // actions (Move / Mark-read / Delete) through the existing mail-action pipeline. Copy/Forward have
    // no client path and Categorize uses a different request, so those are reported as server-side only.
    private async Task RunRulesNowAsync(RulesContext context)
    {
        var mailService = _services.GetService<IMailService>();
        var mimeService = _services.GetService<IMimeFileService>();
        var delegator = _services.GetService<IWinoRequestDelegator>();
        if (mailService == null || delegator == null)
            return;

        var inbox = context.Folders.FirstOrDefault(f => f.SpecialFolderType == SpecialFolderType.Inbox)
                    ?? context.Folders.FirstOrDefault();
        if (inbox == null)
            return;

        var enabledRules = (await context.RuleService.GetRulesAsync(context.Account.Id)).Where(r => r.IsEnabled).ToList();

        // Read-only rules never run: their conditions were only partially mapped (classic Outlook
        // features), and evaluating the lossy remainder over-matches. See RuleRunPlanner.Plan.
        var rules = enabledRules.Where(r => !r.IsReadOnly).ToList();
        var readOnlySkippedCount = enabledRules.Count - rules.Count;

        if (rules.Count == 0)
        {
            await ShowInfoAsync(readOnlySkippedCount > 0
                ? string.Format(Translator.Rules_RunNowReadOnlySkippedNote, readOnlySkippedCount)
                : Translator.Rules_RunNowNoRules);
            return;
        }

        // The whole folder is loaded here; the mail service pages its own id lists, so a large Inbox
        // must not turn into one unbounded SQLite IN() below this point either.
        var messages = await mailService.GetMailsByFolderIdAsync(inbox.Id);
        if (messages.Count == 0)
        {
            await ShowInfoAsync(Translator.Rules_RunNowNoMatches);
            return;
        }

        // Only read MIME (local EML) when a rule actually needs the body or recipients.
        var needsMime = mimeService != null && rules.Any(r => r.Conditions.Any(c =>
            c.Field is RuleConditionField.BodyContains or RuleConditionField.SentTo));

        var facts = new List<RuleMessageFacts>(messages.Count);
        foreach (var message in messages)
        {
            string? body = null;
            IReadOnlyList<string> recipients = Array.Empty<string>();

            if (needsMime)
            {
                try
                {
                    var info = await mimeService!.GetMimeMessageInformationAsync(message.FileId, context.Account.Id);
                    var mime = info?.MimeMessage;
                    if (mime != null)
                    {
                        body = mime.TextBody ?? mime.HtmlBody;
                        recipients = mime.To.OfType<MailboxAddress>()
                            .Concat(mime.Cc.OfType<MailboxAddress>())
                            .Select(mb => mb.Address)
                            .ToList();
                    }
                }
                catch
                {
                    // MIME not cached locally; body/sent-to conditions simply won't match this message.
                }
            }

            facts.Add(new RuleMessageFacts
            {
                MessageId = message.UniqueId,
                FromAddress = message.FromAddress,
                FromName = message.FromName,
                Subject = message.Subject,
                Importance = message.Importance.ToString(),
                HasAttachments = message.HasAttachments,
                Recipients = recipients,
                Body = body
            });
        }

        var plan = RuleRunPlanner.Plan(rules, facts);

        Log.Information(
            "Run rules now: planned {BatchCount} batch(es) matching {MatchedCount} of {MessageCount} message(s) in '{Folder}'; copy/forward skipped={CopyForwardSkipped}, read-only rules skipped={ReadOnlySkipped}",
            plan.Batches.Count, plan.MatchedMessageCount, facts.Count, inbox.FolderName, plan.SkippedCopyForwardCount, readOnlySkippedCount);

        var executableActions = new HashSet<RuleActionType> { RuleActionType.Move, RuleActionType.MarkRead, RuleActionType.Delete };
        var executableBatches = plan.Batches.Where(b => executableActions.Contains(b.Action)).ToList();
        var deferredMessages = plan.Batches.Where(b => !executableActions.Contains(b.Action))
            .SelectMany(b => b.MessageIds).Distinct().Count();
        var skippedCount = plan.SkippedCopyForwardCount + deferredMessages;

        if (executableBatches.Count == 0 && skippedCount == 0)
        {
            await ShowInfoAsync(Translator.Rules_RunNowNoMatches);
            return;
        }

        if (executableBatches.Count == 0)
        {
            await ShowInfoAsync(string.Format(Translator.Rules_RunNowSkippedNote, skippedCount));
            return;
        }

        var executableMessageCount = executableBatches.SelectMany(b => b.MessageIds).Distinct().Count();

        var summaryLines = executableBatches
            .GroupBy(b => b.Action)
            .Select(group => string.Format(Translator.Rules_RunNowSummaryLine,
                group.SelectMany(b => b.MessageIds).Distinct().Count(),
                RuleUiCatalog.ActionLabel(group.Key)))
            .ToList();

        var confirm = new RunRulesNowConfirmDialog(
            string.Format(Translator.Rules_RunNowConfirmHeader, rules.Count, executableMessageCount, inbox.FolderName),
            summaryLines,
            skippedCount > 0 ? string.Format(Translator.Rules_RunNowSkippedNote, skippedCount) : string.Empty,
            readOnlySkippedCount > 0 ? string.Format(Translator.Rules_RunNowReadOnlySkippedNote, readOnlySkippedCount) : string.Empty)
        {
            RequestedTheme = _theme
        };

        if (await _present(confirm) != ContentDialogResult.Primary)
            return;

        var byId = messages.GroupBy(m => m.UniqueId).ToDictionary(g => g.Key, g => g.First());
        foreach (var batch in executableBatches)
        {
            var items = batch.MessageIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (items.Count == 0)
                continue;

            var request = batch.Action switch
            {
                RuleActionType.Move => BuildMoveRequest(context, batch.Value, items),
                RuleActionType.MarkRead => new MailOperationPreperationRequest(MailOperation.MarkAsRead, items),
                RuleActionType.Delete => new MailOperationPreperationRequest(MailOperation.SoftDelete, items),
                _ => null
            };

            if (request != null)
                await delegator.ExecuteAsync(request);
        }

        await ShowInfoAsync(string.Format(Translator.Rules_RunNowComplete, executableMessageCount));
    }

    private static MailOperationPreperationRequest? BuildMoveRequest(RulesContext context, string remoteFolderId, IEnumerable<MailCopy> items)
    {
        var target = context.Folders.FirstOrDefault(f => f.RemoteFolderId == remoteFolderId);
        return target == null ? null : new MailOperationPreperationRequest(MailOperation.Move, items, moveTargetFolder: target);
    }

    private Task ShowInfoAsync(string message, string? title = null)
        => _dialogService.ShowMessageAsync(message, title ?? Translator.Rules_RunNowTitle, WinoCustomMessageDialogIcon.Information);
}
