using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

/// <summary>
/// An Exchange Inbox rule reduced to the predicates and actions both EWS (MS-OXWSRULES) and MAPI (MS-OXORULE) express
/// and Wino's filter model represents. Each predicate list is an OR of its values, the lists are ANDed, as the server
/// evaluates them. A rule with anything else is read with <see cref="IsRepresentable"/> false and is never written.
/// The remote id is the EWS RuleId: the base64 of the rule's PidTagRuleId, so it is the same on both transports.
/// </summary>
internal sealed class ExchangeServerRule
{
    public string? RemoteId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public int Sequence { get; init; }
    public bool StopProcessing { get; init; }
    public bool HasError { get; init; }

    public List<string> FromAddresses { get; } = [];
    public List<string> SubjectContains { get; } = [];
    public List<string> BodyContains { get; } = [];
    public List<string> SenderContains { get; } = [];
    public bool HasAttachments { get; set; }

    /// <summary>Low, Normal or High; null when the rule does not test importance.</summary>
    public string? Importance { get; set; }

    public bool MarkAsRead { get; set; }
    public string? MoveToFolderRemoteId { get; set; }
    public bool SoftDelete { get; set; }
    public bool PermanentDelete { get; set; }

    public bool IsRepresentable { get; set; } = true;

    /// <summary>
    /// Representable and expressible in Wino's filter: several values of one predicate are an OR, which the filter
    /// says only as "any" over a single field. Only such a rule is written back.
    /// </summary>
    public bool IsEditable => IsRepresentable && (!TextGroups().Any(group => group.Values.Count > 1) || GroupCount == 1);

    public static string ToRemoteId(ulong ruleId) => Convert.ToBase64String(BitConverter.GetBytes(ruleId));

    public static bool TryParseRemoteId(string? remoteId, out ulong ruleId)
    {
        ruleId = 0;
        if (string.IsNullOrWhiteSpace(remoteId))
            return false;

        Span<byte> bytes = stackalloc byte[8];
        if (!Convert.TryFromBase64String(remoteId, bytes, out var written) || written != 8)
            return false;

        ruleId = BitConverter.ToUInt64(bytes);
        return true;
    }

    private IEnumerable<(MailFilterConditionField Field, List<string> Values)> TextGroups()
    {
        yield return (MailFilterConditionField.FromAddress, FromAddresses);
        yield return (MailFilterConditionField.Subject, SubjectContains);
        yield return (MailFilterConditionField.PreviewText, BodyContains);
        yield return (MailFilterConditionField.FromName, SenderContains);
    }

    private int GroupCount => TextGroups().Count(group => group.Values.Count > 0) + (HasAttachments ? 1 : 0) + (Importance != null ? 1 : 0);

    private int ConditionCount => TextGroups().Sum(group => group.Values.Count) + (HasAttachments ? 1 : 0) + (Importance != null ? 1 : 0);

    private int ActionCount => (MarkAsRead ? 1 : 0) + (MoveToFolderRemoteId != null ? 1 : 0) + (SoftDelete ? 1 : 0) + (PermanentDelete ? 1 : 0);

    /// <param name="deletedItemsRemoteId">The Deleted Items folder, the target a delete-to-trash action names.</param>
    public MailFilter ToMailFilter(string? deletedItemsRemoteId)
    {
        var multiValued = TextGroups().Any(group => group.Values.Count > 1);
        var matchMode = multiValued && GroupCount == 1 ? MailFilterMatchMode.Any : MailFilterMatchMode.All;

        var filter = new MailFilter
        {
            ManagementType = MailFilterManagementType.Provider,
            RemoteId = RemoteId,
            Name = string.IsNullOrWhiteSpace(Name) ? "Exchange rule" : Name,
            IsEnabled = IsEnabled,
            Sequence = Sequence,
            StopProcessing = StopProcessing,
            ProviderHasError = HasError,
            IsReadOnly = !IsEditable,
            MatchMode = matchMode,
            ProviderSummary = string.Format(Translator.MailFilters_ProviderSummary, ConditionCount, ActionCount)
        };

        foreach (var (field, values) in TextGroups())
        {
            foreach (var value in values)
            {
                filter.Conditions.Add(new MailFilterCondition
                {
                    Order = filter.Conditions.Count,
                    Field = field,
                    Operator = field == MailFilterConditionField.FromAddress ? MailFilterConditionOperator.Equals : MailFilterConditionOperator.Contains,
                    Value = value
                });
            }
        }

        if (HasAttachments)
            filter.Conditions.Add(new MailFilterCondition { Order = filter.Conditions.Count, Field = MailFilterConditionField.HasAttachments, Operator = MailFilterConditionOperator.Equals, Value = bool.TrueString });

        if (Importance != null)
            filter.Conditions.Add(new MailFilterCondition { Order = filter.Conditions.Count, Field = MailFilterConditionField.Importance, Operator = MailFilterConditionOperator.Equals, Value = Importance });

        if (MarkAsRead)
            AddAction(filter, MailFilterActionType.MarkRead, null);
        if (MoveToFolderRemoteId != null)
            AddAction(filter, MailFilterActionType.Move, MoveToFolderRemoteId);
        if (SoftDelete)
            AddAction(filter, MailFilterActionType.SoftDelete, deletedItemsRemoteId);
        if (PermanentDelete)
            AddAction(filter, MailFilterActionType.HardDelete, null);

        return filter;
    }

    private static void AddAction(MailFilter filter, MailFilterActionType type, string? targetRemoteFolderId)
        => filter.Actions.Add(new MailFilterAction { Order = filter.Actions.Count, Type = type, TargetRemoteFolderId = targetRemoteFolderId });

    /// <summary>The server rule for a filter, or <see cref="NotSupportedException"/> when Exchange cannot express it.</summary>
    public static ExchangeServerRule FromMailFilter(MailFilter filter, string? remoteId, int sequence)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var rule = new ExchangeServerRule
        {
            RemoteId = remoteId,
            Name = filter.Name ?? string.Empty,
            IsEnabled = filter.IsEnabled,
            Sequence = sequence,
            StopProcessing = filter.StopProcessing
        };

        var conditions = filter.Conditions.OrderBy(condition => condition.Order).ToList();
        if (conditions.Count == 0)
            throw new NotSupportedException("An Exchange rule needs at least one condition.");

        if (filter.MatchMode == MailFilterMatchMode.Any && conditions.Select(condition => condition.Field).Distinct().Count() > 1)
            throw new NotSupportedException("Exchange rules match any of several values only within one condition.");

        if (filter.MatchMode == MailFilterMatchMode.All && conditions.GroupBy(condition => condition.Field).Any(group => group.Count() > 1))
            throw new NotSupportedException("Exchange rules match several values of one condition as alternatives, not all together.");

        foreach (var condition in conditions)
        {
            var value = condition.Value?.Trim();
            if (string.IsNullOrEmpty(value))
                throw new NotSupportedException("An Exchange rule condition needs a value.");

            switch (condition.Field, condition.Operator)
            {
                case (MailFilterConditionField.FromAddress, MailFilterConditionOperator.Equals):
                    rule.FromAddresses.Add(value);
                    break;
                case (MailFilterConditionField.Subject, MailFilterConditionOperator.Contains):
                    rule.SubjectContains.Add(value);
                    break;
                case (MailFilterConditionField.PreviewText, MailFilterConditionOperator.Contains):
                    rule.BodyContains.Add(value);
                    break;
                case (MailFilterConditionField.FromName, MailFilterConditionOperator.Contains):
                    rule.SenderContains.Add(value);
                    break;
                case (MailFilterConditionField.HasAttachments, MailFilterConditionOperator.Equals) when bool.TryParse(value, out var hasAttachments) && hasAttachments:
                    rule.HasAttachments = true;
                    break;
                case (MailFilterConditionField.Importance, MailFilterConditionOperator.Equals) when ImportanceNames.FirstOrDefault(name => name.Equals(value, StringComparison.OrdinalIgnoreCase)) is { } importance:
                    rule.Importance = importance;
                    break;
                default:
                    throw new NotSupportedException($"Exchange rules cannot test {condition.Field} with {condition.Operator} '{value}'.");
            }
        }

        var actions = filter.Actions.OrderBy(action => action.Order).ToList();
        if (actions.Count == 0)
            throw new NotSupportedException("An Exchange rule needs at least one action.");

        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case MailFilterActionType.MarkRead:
                    rule.MarkAsRead = true;
                    break;
                case MailFilterActionType.Move or MailFilterActionType.Archive or MailFilterActionType.MoveToJunk or MailFilterActionType.MarkAsNotJunk:
                    if (string.IsNullOrWhiteSpace(action.TargetRemoteFolderId))
                        throw new NotSupportedException("A move action needs a target folder.");
                    if (rule.MoveToFolderRemoteId != null && rule.MoveToFolderRemoteId != action.TargetRemoteFolderId)
                        throw new NotSupportedException("An Exchange rule moves a message to one folder only.");
                    rule.MoveToFolderRemoteId = action.TargetRemoteFolderId;
                    break;
                case MailFilterActionType.SoftDelete:
                    rule.SoftDelete = true;
                    break;
                case MailFilterActionType.HardDelete:
                    rule.PermanentDelete = true;
                    break;
                default:
                    throw new NotSupportedException($"Exchange rules cannot {action.Type}.");
            }
        }

        return rule;
    }

    /// <summary>PidTagImportance values 0, 1 and 2 (MS-OXCMSG 2.2.1.11), the names EWS uses for them.</summary>
    public static readonly string[] ImportanceNames = ["Low", "Normal", "High"];

    /// <summary>Copies what the provider does not hold from the filter Wino sent; read-only stays as the server rule reads.</summary>
    public static MailFilter WithLocalMetadata(MailFilter providerFilter, MailFilter source)
    {
        providerFilter.Id = source.Id;
        providerFilter.MailAccountId = source.MailAccountId;
        providerFilter.IsWinoCreated = source.IsWinoCreated;
        providerFilter.CreatedAtUtc = source.CreatedAtUtc;
        return providerFilter;
    }
}
