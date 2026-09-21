using System;
using System.Linq;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Rules;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// Converts between an EWS <see cref="Rule"/> and the provider-neutral <see cref="RemoteInboxRule"/>.
///
/// Wino models a deliberate subset of the EWS rules surface (6 conditions, 6 actions + stop-processing).
/// The <b>fidelity guard</b> (<see cref="TryDescribeUnsupported"/>) flags any rule that carries a
/// predicate/action/exception outside that subset (or that the server reports unsupported/in-error) as
/// read-only, so we never round-trip it and silently drop the parts we cannot represent.
/// </summary>
internal static class ExchangeRuleMapper
{
    // ---- EWS Rule -> DTO -----------------------------------------------------------------------

    internal static RemoteInboxRule ToDto(Rule rule)
    {
        var dto = new RemoteInboxRule
        {
            Id = rule.Id,
            Name = rule.DisplayName,
            Priority = rule.Priority,
            IsEnabled = rule.IsEnabled,
            StopProcessing = rule.Actions.StopProcessingRules
        };

        if (TryDescribeUnsupported(rule, out var reason))
        {
            dto.IsReadOnly = true;
            dto.ReadOnlyReason = reason;
        }

        var c = rule.Conditions;
        if (Has(c.FromAddresses)) dto.Conditions.Add(new RuleConditionModel(RuleConditionField.From, JoinAddresses(c.FromAddresses)));
        if (Has(c.ContainsSubjectStrings)) dto.Conditions.Add(new RuleConditionModel(RuleConditionField.SubjectContains, c.ContainsSubjectStrings.FirstOrDefault()));
        if (Has(c.ContainsBodyStrings)) dto.Conditions.Add(new RuleConditionModel(RuleConditionField.BodyContains, c.ContainsBodyStrings.FirstOrDefault()));
        if (Has(c.SentToAddresses)) dto.Conditions.Add(new RuleConditionModel(RuleConditionField.SentTo, JoinAddresses(c.SentToAddresses)));
        if (c.Importance.HasValue) dto.Conditions.Add(new RuleConditionModel(RuleConditionField.Importance, c.Importance.Value.ToString()));
        if (c.HasAttachments) dto.Conditions.Add(new RuleConditionModel(RuleConditionField.HasAttachment, null));

        var a = rule.Actions;
        if (a.MoveToFolder != null) dto.Actions.Add(new RuleActionModel(RuleActionType.Move, a.MoveToFolder.UniqueId));
        if (a.CopyToFolder != null) dto.Actions.Add(new RuleActionModel(RuleActionType.Copy, a.CopyToFolder.UniqueId));
        if (Has(a.AssignCategories)) dto.Actions.Add(new RuleActionModel(RuleActionType.Categorize, a.AssignCategories.FirstOrDefault()));
        if (a.MarkAsRead) dto.Actions.Add(new RuleActionModel(RuleActionType.MarkRead, null));
        if (Has(a.ForwardToRecipients)) dto.Actions.Add(new RuleActionModel(RuleActionType.Forward, JoinAddresses(a.ForwardToRecipients)));
        if (a.Delete) dto.Actions.Add(new RuleActionModel(RuleActionType.Delete, null));

        return dto;
    }

    // ---- DTO -> EWS Rule -----------------------------------------------------------------------

    internal static Rule ToEwsRule(RemoteInboxRule dto)
    {
        var rule = new Rule
        {
            DisplayName = dto.Name,
            // EWS priorities are 1-based; clamp so a not-yet-prioritized new rule is valid.
            Priority = dto.Priority > 0 ? dto.Priority : 1,
            IsEnabled = dto.IsEnabled
        };

        // Id is set only for an existing rule (Set/Delete); a create leaves it for the server to assign.
        if (!string.IsNullOrEmpty(dto.Id))
            rule.Id = dto.Id;

        foreach (var condition in dto.Conditions ?? Enumerable.Empty<RuleConditionModel>())
            ApplyCondition(rule.Conditions, condition);

        foreach (var action in dto.Actions ?? Enumerable.Empty<RuleActionModel>())
            ApplyAction(rule.Actions, action);

        if (dto.StopProcessing)
            rule.Actions.StopProcessingRules = true;

        return rule;
    }

    private static void ApplyCondition(RulePredicates predicates, RuleConditionModel condition)
    {
        switch (condition.Field)
        {
            case RuleConditionField.From:
                AddAddresses(predicates.FromAddresses, condition.Value);
                break;
            case RuleConditionField.SentTo:
                AddAddresses(predicates.SentToAddresses, condition.Value);
                break;
            case RuleConditionField.SubjectContains:
                if (!string.IsNullOrWhiteSpace(condition.Value)) predicates.ContainsSubjectStrings.Add(condition.Value.Trim());
                break;
            case RuleConditionField.BodyContains:
                if (!string.IsNullOrWhiteSpace(condition.Value)) predicates.ContainsBodyStrings.Add(condition.Value.Trim());
                break;
            case RuleConditionField.Importance:
                if (Enum.TryParse<Importance>(condition.Value, ignoreCase: true, out var importance)) predicates.Importance = importance;
                break;
            case RuleConditionField.HasAttachment:
                predicates.HasAttachments = true;
                break;
        }
    }

    private static void ApplyAction(RuleActions actions, RuleActionModel action)
    {
        switch (action.Type)
        {
            case RuleActionType.Move:
                if (!string.IsNullOrEmpty(action.Value)) actions.MoveToFolder = new FolderId(action.Value);
                break;
            case RuleActionType.Copy:
                if (!string.IsNullOrEmpty(action.Value)) actions.CopyToFolder = new FolderId(action.Value);
                break;
            case RuleActionType.Categorize:
                if (!string.IsNullOrWhiteSpace(action.Value)) actions.AssignCategories.Add(action.Value.Trim());
                break;
            case RuleActionType.MarkRead:
                actions.MarkAsRead = true;
                break;
            case RuleActionType.Forward:
                AddAddresses(actions.ForwardToRecipients, action.Value);
                break;
            case RuleActionType.Delete:
                actions.Delete = true;
                break;
        }
    }

    // ---- Fidelity guard ------------------------------------------------------------------------

    /// <summary>True (with a human reason) when the rule has anything outside the modeled subset.</summary>
    private static bool TryDescribeUnsupported(Rule rule, out string reason)
    {
        if (rule.IsNotSupported) { reason = "uses features this server reports as unsupported"; return true; }
        if (rule.IsInError) { reason = "is flagged in error by the server"; return true; }
        if (HasAnyPredicate(rule.Exceptions)) { reason = "has exception clauses Wino Mail can't edit"; return true; }
        if (HasUnsupportedPredicate(rule.Conditions, out reason)) return true;
        if (HasUnsupportedAction(rule.Actions, out reason)) return true;

        reason = null;
        return false;
    }

    private static bool HasUnsupportedPredicate(RulePredicates p, out string reason)
    {
        reason = "uses a condition Wino Mail can't edit";

        if (Has(p.Categories) || Has(p.ContainsHeaderStrings) || Has(p.ContainsRecipientStrings)
            || Has(p.ContainsSenderStrings) || Has(p.ContainsSubjectOrBodyStrings)
            || Has(p.FromConnectedAccounts) || Has(p.ItemClasses) || Has(p.MessageClassifications))
            return true;

        if (p.FlaggedForAction.HasValue || p.Sensitivity.HasValue)
            return true;

        if (p.NotSentToMe || p.SentCcMe || p.SentOnlyToMe || p.SentToMe || p.SentToOrCcMe)
            return true;

        if (p.IsApprovalRequest || p.IsAutomaticForward || p.IsAutomaticReply || p.IsEncrypted
            || p.IsMeetingRequest || p.IsMeetingResponse || p.IsNonDeliveryReport
            || p.IsPermissionControlled || p.IsReadReceipt || p.IsSigned || p.IsVoicemail)
            return true;

        if (p.WithinDateRange?.Start != null || p.WithinDateRange?.End != null)
            return true;

        if (p.WithinSizeRange?.MinimumSize != null || p.WithinSizeRange?.MaximumSize != null)
            return true;

        // Modeled, but multi-valued beyond what the single-value editor can faithfully represent.
        if (Count(p.ContainsSubjectStrings) > 1 || Count(p.ContainsBodyStrings) > 1)
        {
            reason = "matches multiple subject/body terms";
            return true;
        }

        return false;
    }

    private static bool HasUnsupportedAction(RuleActions a, out string reason)
    {
        reason = "uses an action Wino Mail can't edit";

        if (Has(a.ForwardAsAttachmentToRecipients) || Has(a.RedirectToRecipients))
            return true;

        // SendSMSAlertToRecipients is a Collection<MobilePhone>, not an EmailAddressCollection.
        if (a.SendSMSAlertToRecipients != null && a.SendSMSAlertToRecipients.Count > 0)
            return true;

        if (a.MarkImportance.HasValue || a.PermanentDelete || a.ServerReplyWithMessage != null)
            return true;

        if (Count(a.AssignCategories) > 1)
        {
            reason = "assigns multiple categories";
            return true;
        }

        return false;
    }

    /// <summary>True when any condition we would recognise is present in an exception clause (we model no exceptions).</summary>
    private static bool HasAnyPredicate(RulePredicates p)
    {
        if (p == null) return false;

        return Has(p.FromAddresses) || Has(p.SentToAddresses) || Has(p.ContainsSubjectStrings)
            || Has(p.ContainsBodyStrings) || Has(p.Categories) || Has(p.ContainsHeaderStrings)
            || Has(p.ContainsRecipientStrings) || Has(p.ContainsSenderStrings) || Has(p.ContainsSubjectOrBodyStrings)
            || Has(p.FromConnectedAccounts) || Has(p.ItemClasses) || Has(p.MessageClassifications)
            || p.Importance.HasValue || p.HasAttachments || p.FlaggedForAction.HasValue || p.Sensitivity.HasValue
            || p.NotSentToMe || p.SentCcMe || p.SentOnlyToMe || p.SentToMe || p.SentToOrCcMe
            || p.IsApprovalRequest || p.IsAutomaticForward || p.IsAutomaticReply || p.IsEncrypted
            || p.IsMeetingRequest || p.IsMeetingResponse || p.IsNonDeliveryReport || p.IsPermissionControlled
            || p.IsReadReceipt || p.IsSigned || p.IsVoicemail
            || p.WithinDateRange?.Start != null || p.WithinDateRange?.End != null
            || p.WithinSizeRange?.MinimumSize != null || p.WithinSizeRange?.MaximumSize != null;
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static bool Has(StringList list) => list != null && list.Count > 0;

    private static bool Has(EmailAddressCollection list) => list != null && list.Count > 0;

    private static int Count(StringList list) => list?.Count ?? 0;

    private static string JoinAddresses(EmailAddressCollection list) =>
        string.Join(", ", list.Select(e => e.Address).Where(s => !string.IsNullOrWhiteSpace(s)));

    private static void AddAddresses(EmailAddressCollection collection, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        foreach (var part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                collection.Add(new EmailAddress(trimmed));
        }
    }
}
