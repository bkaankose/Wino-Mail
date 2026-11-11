using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services.Assistant;

/// <summary>
/// The steps that change the app rather than items: a new Wino mail rule (created on the account's inbox, then opened
/// in the rule editor for the user to see), one of the closed set of settings switched on or off, and a page opened.
/// The plan was confirmed before any of these run; a rule that deletes had its own confirmation.
/// </summary>
public sealed partial class AgentPlanExecutor
{
    private async Task<StepOutcome> CreateMailRuleAsync(ResolvedStep step, CancellationToken ct)
    {
        if (step.Args is not StepArgs.MailRuleArgs rule || rule.Actions.Count == 0)
            return Failed(step, Translator.VoiceIssue_NotUnderstood);

        var account = await accountService.GetAccountAsync(rule.AccountId).ConfigureAwait(false);
        if (account is null)
            return Failed(step, Translator.VoiceIssue_AccountUnnamed);

        // A Wino rule runs on the mail arriving in one folder: new mail arrives in the inbox.
        var inbox = await folderService.GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Inbox).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(inbox?.RemoteFolderId))
            return Failed(step, Translator.AssistantIssue_RuleNoInbox);

        // A Wino rule moves a mail once and does nothing else in that rule: marking or flagging and a move become two
        // rules on the same conditions, the marking one first (rules run in creation order). Two moves cannot both happen.
        var terminal = rule.Actions.Where(a => IsFolderChanging(a.Type)).ToList();
        if (terminal.Count > 1)
            return Failed(step, Translator.AssistantIssue_RuleTwoMoves);

        var marking = rule.Actions.Where(a => !IsFolderChanging(a.Type)).ToList();
        var groups = new List<List<MailRuleAction>>();
        if (marking.Count > 0) groups.Add(marking);
        if (terminal.Count > 0) groups.Add(terminal);

        var name = string.IsNullOrWhiteSpace(rule.Name) ? Gloss(step) : rule.Name.Trim();
        var filters = services.GetRequiredService<IMailFilterService>();
        MailFilter? first = null;
        foreach (var group in groups)
        {
            var actions = new List<MailFilterAction>();
            foreach (var action in group)
            {
                var target = await RuleTargetAsync(account.Id, action).ConfigureAwait(false);
                if (target.Missing)
                    return Failed(step, Translator.AssistantIssue_RuleFolderMissing);

                actions.Add(new MailFilterAction { Id = Guid.NewGuid(), Order = actions.Count, Type = action.Type, TargetRemoteFolderId = target.RemoteId });
            }

            var conditions = rule.Conditions
                .Where(c => !string.IsNullOrWhiteSpace(c.Value))
                .Select((c, i) => new MailFilterCondition { Id = Guid.NewGuid(), Order = i, Field = c.Field, Operator = c.Operator, Value = c.Value })
                .ToList();

            var now = DateTime.UtcNow;
            var filter = new MailFilter
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                ManagementType = MailFilterManagementType.WinoLocal,
                Name = groups.Count > 1 ? $"{name} ({groups.IndexOf(group) + 1})" : name,
                SourceRemoteFolderId = inbox.RemoteFolderId,
                MatchMode = rule.Match,
                IsEnabled = true,
                IsWinoCreated = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Conditions = conditions,
                Actions = actions,
            };

            await filters.CreateFilterAsync(filter, ct).ConfigureAwait(false);
            first ??= filter;
        }

        if (first is not null && Host is { } host)
            await host.OpenMailRuleAsync(account.Id, first.Id).ConfigureAwait(false);

        return Done(step, [], string.Format(CultureInfo.CurrentCulture, Translator.AssistantResult_RuleCreated, name));
    }

    /// <summary>The actions that take a mail out of the folder the rule watches: one per Wino rule, and alone in it.</summary>
    private static bool IsFolderChanging(MailFilterActionType type)
        => type is MailFilterActionType.Move or MailFilterActionType.Archive or MailFilterActionType.MoveToJunk
            or MailFilterActionType.MarkAsNotJunk or MailFilterActionType.SoftDelete or MailFilterActionType.HardDelete;

    /// <summary>
    /// The folder a rule action moves to, as the rule model stores it: the chosen folder for a move, the account's special
    /// folder for archive, junk, not junk and the trash, nothing for the actions that keep the mail where it is.
    /// </summary>
    private async Task<(string? RemoteId, bool Missing)> RuleTargetAsync(Guid accountId, MailRuleAction action)
    {
        if (action.Type == MailFilterActionType.Move)
        {
            var folder = action.TargetFolderId is { } id ? await folderService.GetFolderAsync(id).ConfigureAwait(false) : null;
            return string.IsNullOrWhiteSpace(folder?.RemoteFolderId) ? (null, true) : (folder.RemoteFolderId, false);
        }

        var special = action.Type switch
        {
            MailFilterActionType.Archive => SpecialFolderType.Archive,
            MailFilterActionType.MoveToJunk => SpecialFolderType.Junk,
            MailFilterActionType.MarkAsNotJunk => SpecialFolderType.Inbox,
            MailFilterActionType.SoftDelete => SpecialFolderType.Deleted,
            _ => (SpecialFolderType?)null,
        };
        if (special is null)
            return (null, false);

        var target = await folderService.GetSpecialFolderByAccountIdAsync(accountId, special.Value).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(target?.RemoteFolderId) ? (null, true) : (target.RemoteFolderId, false);
    }

    private Task<StepOutcome> ChangeSettingAsync(ResolvedStep step)
    {
        if (step.Args is not StepArgs.SettingArgs setting)
            return Task.FromResult(Failed(step, Translator.VoiceIssue_NotUnderstood));

        switch (setting.Setting)
        {
            case AgentSetting.NewMailNotifications: preferencesService.AreNewMailNotificationsEnabled = setting.Enabled; break;
            case AgentSetting.QuietHours: preferencesService.AreQuietHoursEnabled = setting.Enabled; break;
            case AgentSetting.VoiceAutoConfirm: preferencesService.IsVoiceAutoConfirmEnabled = setting.Enabled; break;
            case AgentSetting.CalendarReminders: preferencesService.AreCalendarRemindersEnabled = setting.Enabled; break;
            case AgentSetting.TaskReminders: preferencesService.AreTaskRemindersEnabled = setting.Enabled; break;
            default: return Task.FromResult(Failed(step, Translator.VoiceIssue_NotUnderstood));
        }

        var template = setting.Enabled ? Translator.AssistantResult_SettingOn : Translator.AssistantResult_SettingOff;
        return Task.FromResult(Done(step, [], string.Format(CultureInfo.CurrentCulture, template, SettingName(setting.Setting))));
    }

    private static string SettingName(AgentSetting setting) => setting switch
    {
        AgentSetting.NewMailNotifications => Translator.AssistantSetting_NewMailNotifications,
        AgentSetting.QuietHours => Translator.AssistantSetting_QuietHours,
        AgentSetting.VoiceAutoConfirm => Translator.AssistantSetting_VoiceAutoConfirm,
        AgentSetting.CalendarReminders => Translator.AssistantSetting_CalendarReminders,
        _ => Translator.AssistantSetting_TaskReminders,
    };

    private async Task<StepOutcome> OpenViewAsync(ResolvedStep step)
    {
        if (step.Args is not StepArgs.ViewArgs view)
            return Failed(step, Translator.VoiceIssue_NotUnderstood);
        if (Host is not { } host)
            return Failed(step, Translator.VoiceIssue_NotUnderstood);

        await host.OpenViewAsync(view.View, view.AccountId).ConfigureAwait(false);
        return Done(step, [], Translator.AssistantResult_ViewOpened);
    }
}
