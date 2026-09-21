#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Rules;
using Wino.Mapi.Restrictions;
using Wino.Mapi.Rops;
using Wino.Mapi.Rules;
using RuleActionType = Wino.Core.Domain.Enums.RuleActionType;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// Converts between a rules-table row (<see cref="MapiRuleInfo"/>) and the provider-neutral
/// <see cref="RemoteInboxRule"/>, and back to a <see cref="MapiRuleDefinition"/> for writing.
///
/// Same modeled subset as the EWS mapper (6 conditions, 6 actions, stop-processing) and the same
/// fidelity guard: any rule whose condition or actions fall outside it is returned read-only with a
/// reason, never rewritten. Wire encodings recognised on read and produced on write:
///   From      = PROPERTY EQ PidTagSenderSearchKey "SMTP:ADDR" and/or CONTENT on PidTagSenderSmtpAddress,
///               optionally wrapped in RES_COMMENT (Outlook), several joined by OR
///   SentTo    = SUBOBJECT PidTagMessageRecipients over the same shapes on PidTagSearchKey / PidTagSmtpAddress
///   Subject   = CONTENT SUBSTRING|IGNORECASE PidTagSubject (PidTagNormalizedSubject accepted)
///   Body      = CONTENT SUBSTRING|IGNORECASE PidTagBody
///   Importance= PROPERTY EQ PidTagImportance
///   Attachment= BITMASK NEZ PidTagMessageFlags 0x10 (PROPERTY EQ PidTagHasAttachments TRUE accepted)
///   no condition = EXIST PidTagMessageClass
/// </summary>
internal static class MapiRuleMapper
{
    /// <summary>Folder keys in the DTO are the folder's remote id, which is what the rule editor's folder picker keys on.</summary>
    internal delegate string? FolderKeyResolver(ulong folderId);
    internal delegate ulong? FolderIdResolver(string folderKey);

    internal static string ToRuleId(ulong ruleId) => ruleId.ToString("X16");

    internal static bool TryParseRuleId(string? id, out ulong ruleId)
        => ulong.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out ruleId);

    // ---- Row -> DTO ----------------------------------------------------------------------------

    internal static RemoteInboxRule ToDto(MapiRuleInfo rule, FolderKeyResolver folderKey, uint? keywordsTag)
    {
        var dto = new RemoteInboxRule
        {
            Id = ToRuleId(rule.RuleId),
            Name = rule.Name,
            Priority = (int)Math.Min(rule.Sequence, int.MaxValue),
            IsEnabled = rule.IsEnabled,
            StopProcessing = rule.StopsProcessing
        };

        var reasons = new List<string>();

        if (rule.HasError)
            reasons.Add("The server reports the rule is in error.");
        if ((rule.State & MapiRuleInfo.StateOnlyWhenOof) != 0)
            reasons.Add("The rule only runs while out of office.");

        if (rule.Condition is null)
            reasons.Add("The rule's condition could not be read.");
        else
            ReadConditions(rule.Condition, dto, reasons);

        if (rule.Actions is null)
            reasons.Add("The rule's actions could not be read.");
        else
            ReadActions(rule.Actions, dto, folderKey, keywordsTag, reasons);

        if (reasons.Count > 0)
        {
            dto.IsReadOnly = true;
            dto.ReadOnlyReason = string.Join(" ", reasons.Distinct());
        }

        return dto;
    }

    private static void ReadConditions(RestrictionNode condition, RemoteInboxRule dto, List<string> reasons)
    {
        // "Apply to all messages": the store's own encoding of an always-true condition.
        if (condition.Kind == RestrictionKind.Exist && condition.PropertyTag == PropertyTags.MessageClass)
            return;

        var terms = condition.Kind == RestrictionKind.And ? condition.Children : [condition];
        foreach (var term in terms)
        {
            if (TryReadAddressTerm(term, dto, reasons))
                continue;

            switch (term.Kind)
            {
                case RestrictionKind.Content when term.Value is string text && term.PropertyTag is PropertyTags.Subject or PropertyTags.NormalizedSubject:
                    dto.Conditions.Add(new RuleConditionModel(RuleConditionField.SubjectContains, text));
                    break;

                case RestrictionKind.Content when term.Value is string text && term.PropertyTag == PropertyTags.Body:
                    dto.Conditions.Add(new RuleConditionModel(RuleConditionField.BodyContains, text));
                    break;

                case RestrictionKind.Property when term.PropertyTag == PropertyTags.Importance && term.RelOp == RestrictionOps.RelOpEq && term.Value is uint importance:
                    dto.Conditions.Add(new RuleConditionModel(RuleConditionField.Importance, ImportanceName(importance)));
                    break;

                case RestrictionKind.BitMask when term.PropertyTag == PropertyTags.MessageFlags && term.RelOp == RestrictionOps.BitMaskNeZ && term.Mask == PropertyTags.MessageFlagHasAttach:
                case RestrictionKind.Property when term.PropertyTag == PropertyTags.HasAttachments && term.Value is true:
                    dto.Conditions.Add(new RuleConditionModel(RuleConditionField.HasAttachment, null));
                    break;

                case RestrictionKind.Exist:
                    // A guard Outlook adds beside some predicates; it changes nothing the client shows.
                    break;

                default:
                    reasons.Add(term.Kind == RestrictionKind.Not
                        ? "The rule has an exception clause."
                        : $"The rule's condition uses {DescribeTerm(term)}, which this client does not edit.");
                    break;
            }
        }
    }

    /// <summary>Recognises a From or SentTo term in any of its wire shapes; false when the term is something else.</summary>
    private static bool TryReadAddressTerm(RestrictionNode term, RemoteInboxRule dto, List<string> reasons)
    {
        var recipients = false;
        var node = term;
        if (node.Kind == RestrictionKind.SubObject)
        {
            if (node.PropertyTag != PropertyTags.MessageRecipients || node.Children.Count != 1)
                return false;
            recipients = true;
            node = node.Children[0];
        }

        var addresses = new List<string>();
        if (!CollectAddresses(node, recipients, addresses))
            return false;

        if (addresses.Count == 0)
            return false;

        var field = recipients ? RuleConditionField.SentTo : RuleConditionField.From;
        var existing = dto.Conditions.FirstOrDefault(c => c.Field == field);
        var joined = string.Join(", ", addresses.Distinct(StringComparer.OrdinalIgnoreCase));
        if (existing is null)
            dto.Conditions.Add(new RuleConditionModel(field, joined));
        else
            existing.Value = existing.Value + ", " + joined;

        return true;
    }

    private static bool CollectAddresses(RestrictionNode node, bool recipients, List<string> addresses)
    {
        switch (node.Kind)
        {
            case RestrictionKind.Or:
                return node.Children.Count > 0 && node.Children.All(child => CollectAddresses(child, recipients, addresses));

            case RestrictionKind.Comment:
            {
                if (node.Children.Count != 1)
                    return false;

                // Outlook keeps a readable address beside the search key; prefer it when it is SMTP.
                var smtp = node.CommentValues.FirstOrDefault(v => v.Tag == PropertyTags.SmtpAddress)?.Value as string;
                var inner = new List<string>();
                if (!CollectAddresses(node.Children[0], recipients, inner))
                    return false;

                if (!string.IsNullOrWhiteSpace(smtp))
                    addresses.Add(smtp.Trim());
                else
                    addresses.AddRange(inner);
                return true;
            }

            case RestrictionKind.Property when node.RelOp == RestrictionOps.RelOpEq && node.Value is byte[] key
                                              && node.PropertyTag == (recipients ? PropertyTags.SearchKey : PropertyTags.SenderSearchKey):
                addresses.Add(SearchKeyToAddress(key));
                return true;

            case RestrictionKind.Content when !recipients && node.Value is string sender
                                             && node.PropertyTag is PropertyTags.SenderSmtpAddress or PropertyTags.SenderEmailAddress or PropertyTags.SentRepresentingSmtpAddress:
                addresses.Add(sender.Trim());
                return true;

            case RestrictionKind.Content when recipients && node.Value is string recipient
                                             && node.PropertyTag is PropertyTags.SmtpAddress or PropertyTags.EmailAddress:
                addresses.Add(recipient.Trim());
                return true;

            default:
                return false;
        }
    }

    /// <summary>"SMTP:USER@DOMAIN" becomes user@domain; an "EX:/O=..." key is kept as the DN.</summary>
    private static string SearchKeyToAddress(byte[] key)
    {
        var text = Encoding.ASCII.GetString(key).TrimEnd('\0');
        if (text.StartsWith("SMTP:", StringComparison.OrdinalIgnoreCase))
            return text.Substring(5).ToLowerInvariant();
        if (text.StartsWith("EX:", StringComparison.OrdinalIgnoreCase))
            return text.Substring(3);
        return text;
    }

    private static void ReadActions(List<RuleAction> actions, RemoteInboxRule dto, FolderKeyResolver folderKey, uint? keywordsTag, List<string> reasons)
    {
        foreach (var action in actions)
        {
            switch (action.Type)
            {
                case Wino.Mapi.Rules.RuleActionType.Move:
                case Wino.Mapi.Rules.RuleActionType.Copy:
                {
                    if (action.ServerFolderId is not { } folderId)
                    {
                        reasons.Add("The rule targets a folder in another mailbox.");
                        break;
                    }

                    var key = folderKey(folderId);
                    if (key is null)
                    {
                        reasons.Add("The rule targets a folder this client has not synced.");
                        break;
                    }

                    dto.Actions.Add(new RuleActionModel(action.Type == Wino.Mapi.Rules.RuleActionType.Move ? RuleActionType.Move : RuleActionType.Copy, key));
                    break;
                }

                case Wino.Mapi.Rules.RuleActionType.Delete:
                    dto.Actions.Add(new RuleActionModel(RuleActionType.Delete, null));
                    break;

                case Wino.Mapi.Rules.RuleActionType.MarkAsRead:
                    dto.Actions.Add(new RuleActionModel(RuleActionType.MarkRead, null));
                    break;

                case Wino.Mapi.Rules.RuleActionType.Forward when action.Recipients is { Count: > 0 } && action.Flavor == 0:
                    dto.Actions.Add(new RuleActionModel(RuleActionType.Forward, string.Join(", ", action.Recipients.Select(r => r.Address).Where(a => a.Length > 0))));
                    break;

                case Wino.Mapi.Rules.RuleActionType.Forward:
                    reasons.Add(action.Flavor switch
                    {
                        0x01 => "The rule redirects messages.",
                        0x04 => "The rule forwards messages as attachments.",
                        _ => "The rule's forwarding recipients could not be read.",
                    });
                    break;

                case Wino.Mapi.Rules.RuleActionType.Tag when keywordsTag is { } tag && action.TagPropertyTag == tag && action.TagValue is string[] categories:
                    if (categories.Length == 1)
                        dto.Actions.Add(new RuleActionModel(RuleActionType.Categorize, categories[0]));
                    else
                        reasons.Add("The rule assigns more than one category.");
                    break;

                default:
                    reasons.Add($"The rule has a {action.Type} action, which this client does not edit.");
                    break;
            }
        }
    }

    // ---- DTO -> definition ---------------------------------------------------------------------

    /// <summary>Throws <see cref="NotSupportedException"/> with a user-facing message when the DTO cannot be written.</summary>
    internal static MapiRuleDefinition ToDefinition(RemoteInboxRule dto, FolderIdResolver folderId, uint? keywordsTag)
    {
        var terms = new List<RestrictionNode>();
        foreach (var condition in dto.Conditions ?? Enumerable.Empty<RuleConditionModel>())
        {
            var term = BuildCondition(condition, out var error);
            if (error is not null)
                throw new NotSupportedException(error);
            if (term is not null)
                terms.Add(term);
        }

        var restriction = terms.Count switch
        {
            0 => RestrictionNode.Exist(PropertyTags.MessageClass),
            1 => terms[0],
            _ => RestrictionNode.And(terms.ToArray()),
        };

        var actions = new List<RuleAction>();
        foreach (var action in dto.Actions ?? Enumerable.Empty<RuleActionModel>())
        {
            actions.Add(BuildAction(action, folderId, keywordsTag));
        }

        if (actions.Count == 0)
            throw new NotSupportedException("A rule needs at least one action.");

        return new MapiRuleDefinition(
            dto.Name ?? string.Empty,
            (uint)Math.Max(dto.Priority, 1),
            dto.IsEnabled,
            dto.StopProcessing,
            restriction,
            actions);
    }

    internal static bool NeedsKeywordsTag(RemoteInboxRule dto)
        => dto.Actions?.Any(a => a.Type == RuleActionType.Categorize) == true;

    private static RestrictionNode? BuildCondition(RuleConditionModel condition, out string? error)
    {
        error = null;
        switch (condition.Field)
        {
            case RuleConditionField.From:
            {
                var terms = SplitAddresses(condition.Value).Select(SenderTerm).ToArray();
                return terms.Length switch
                {
                    0 => null,
                    1 => terms[0],
                    _ => RestrictionNode.Or(terms)
                };
            }

            case RuleConditionField.SentTo:
            {
                var terms = SplitAddresses(condition.Value).Select(RecipientTerm).ToArray();
                if (terms.Length == 0)
                    return null;
                return RestrictionNode.SubObject(PropertyTags.MessageRecipients, terms.Length == 1 ? terms[0] : RestrictionNode.Or(terms));
            }

            case RuleConditionField.SubjectContains:
                return string.IsNullOrWhiteSpace(condition.Value) ? null : RestrictionNode.Content(PropertyTags.Subject, condition.Value.Trim());

            case RuleConditionField.BodyContains:
                return string.IsNullOrWhiteSpace(condition.Value) ? null : RestrictionNode.Content(PropertyTags.Body, condition.Value.Trim());

            case RuleConditionField.Importance:
                if (!TryParseImportance(condition.Value, out var importance))
                {
                    error = $"Unknown importance '{condition.Value}'.";
                    return null;
                }
                return RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.Importance, importance);

            case RuleConditionField.HasAttachment:
                return RestrictionNode.BitMask(RestrictionOps.BitMaskNeZ, PropertyTags.MessageFlags, PropertyTags.MessageFlagHasAttach);

            default:
                error = $"Condition {condition.Field} is not supported over MAPI.";
                return null;
        }
    }

    /// <summary>
    /// The shape Outlook writes for "from people": a comment carrying the recipient (one-off EntryID,
    /// display name, address) around a single search-key equality. Exchange's own rule parser (what OWA
    /// and EWS load the table through) knows this shape; anything richer inside the comment risks the
    /// whole table failing to load there. Internal senders match only when the address the user typed
    /// is the one the store puts in the search key, which for SMTP recipients is the SMTP form.
    /// </summary>
    private static RestrictionNode SenderTerm(string address)
        => RestrictionNode.Comment(CommentValues(address),
            RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.SenderSearchKey, RuleActions.SmtpSearchKey(address)));

    private static RestrictionNode RecipientTerm(string address)
        => RestrictionNode.Comment(CommentValues(address),
            RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.SearchKey, RuleActions.SmtpSearchKey(address)));

    private static List<TaggedPropertyValue> CommentValues(string address) =>
    [
        TaggedPropertyValue.Long(PropertyTags.RuleCommentType, 1),
        TaggedPropertyValue.Binary(PropertyTags.EntryId, RuleActions.OneOffEntryId(address, address)),
        TaggedPropertyValue.Unicode(PropertyTags.DisplayName, address),
        TaggedPropertyValue.Unicode(PropertyTags.AddressType, "SMTP"),
        TaggedPropertyValue.Unicode(PropertyTags.EmailAddress, address),
        TaggedPropertyValue.Unicode(PropertyTags.SmtpAddress, address),
    ];

    private static RuleAction BuildAction(RuleActionModel action, FolderIdResolver folderId, uint? keywordsTag)
    {
        switch (action.Type)
        {
            case RuleActionType.Move:
            case RuleActionType.Copy:
            {
                var id = string.IsNullOrEmpty(action.Value) ? null : folderId(action.Value);
                if (id is not { } target)
                    throw new NotSupportedException("The rule's target folder is not known to this client; sync folders and try again.");
                return action.Type == RuleActionType.Move ? RuleAction.MoveTo(target) : RuleAction.CopyTo(target);
            }

            case RuleActionType.Delete:
                return RuleAction.Delete();

            case RuleActionType.MarkRead:
                return RuleAction.MarkAsRead();

            case RuleActionType.Forward:
            {
                var recipients = SplitAddresses(action.Value).Select(a => new RuleRecipient(a, a)).ToList();
                if (recipients.Count == 0)
                    throw new NotSupportedException("A forward action needs at least one address.");
                return RuleAction.ForwardTo(recipients);
            }

            case RuleActionType.Categorize:
            {
                if (string.IsNullOrWhiteSpace(action.Value))
                    throw new NotSupportedException("A categorize action needs a category.");
                if (keywordsTag is not { } tag)
                    throw new NotSupportedException("The store did not provide the categories property.");
                return RuleAction.TagWith(tag, new[] { action.Value.Trim() });
            }

            default:
                throw new NotSupportedException($"Action {action.Type} is not supported over MAPI.");
        }
    }

    // ---- Helpers -------------------------------------------------------------------------------

    private static IEnumerable<string> SplitAddresses(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Enumerable.Empty<string>()
            : value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0);

    private static string ImportanceName(uint importance) => importance switch
    {
        0 => "Low",
        2 => "High",
        _ => "Normal",
    };

    private static bool TryParseImportance(string? value, out uint importance)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "low": importance = 0; return true;
            case "normal": importance = 1; return true;
            case "high": importance = 2; return true;
            default: importance = 1; return false;
        }
    }

    private static string DescribeTerm(RestrictionNode term)
        => term.PropertyTag is { } tag ? $"{term.Kind} on {PropertyTags.Describe(tag)}" : term.Kind.ToString();
}
