using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Providers.Exchange.MAPI;
using Wino.Providers.Exchange.MAPI.Restrictions;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Providers.Exchange.MAPI.Rules;
using Wino.Providers.Exchange.Synchronizers.Exchange;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

// Inbox rules over the Inbox's rules table (MS-OXORULE 2.2.1, RopModifyRules 3.1.4.2.1). Conditions and actions are
// read and written in the shapes Exchange itself stores for the EWS predicates (ExchangeServerRule), so a rule written
// here reads back the same over EWS. Anything else is read-only and never written.
public sealed partial class MapiExchangeSynchronizer
{
    // The provider Exchange 2010 and later stores for Inbox rules its own rule management writes (EWS, OWA, EMS; seen
    // on every rule with a From predicate), MS-OXORULE 2.2.1.3.1.5 note <2>. Those rules are the user's Inbox rules
    // as much as RuleOrganizer ones and EWS edits them, so they are editable here too. An update sends the provider
    // back unchanged (MapiRulesOperations.UpdateRuleAsync), so the rule stays Exchange's.
    private const string ExchangeMailboxRulesProvider = "ExchangeMailboxRules14";

    private const uint EditableStateBits = MapiRuleInfo.StateEnabled | MapiRuleInfo.StateError | MapiRuleInfo.StateExitLevel;
    private const uint MessageFlagHasAttachment = 0x00000010;   // MSGFLAG_HASATTACH (MS-OXCMSG 2.2.1.6)
    private const string SmtpSearchKeyPrefix = "SMTP:";

    private sealed record RuleFolders(ulong InboxFolderId, ulong DeletedItemsFolderId, string DeletedItemsRemoteId, IReadOnlyDictionary<string, string> RemoteIdByEntryIdHex);

    public override async Task<IReadOnlyList<MailFilter>> GetProviderFiltersAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var folders = await GetRuleFoldersAsync(lease.Session).ConfigureAwait(false);
        var rules = await ReadInboxRulesAsync(lease.Session, folders, cancellationToken).ConfigureAwait(false);
        var senders = await ResolveRuleSendersAsync(rules, cancellationToken).ConfigureAwait(false);
        return rules.Select(rule => FromMapiRule(rule, folders, senders).ToMailFilter(folders.DeletedItemsRemoteId)).ToList();
    }

    public override async Task<MailFilter> CreateProviderFilterAsync(MailFilter filter, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        var folders = await GetRuleFoldersAsync(session).ConfigureAwait(false);

        var before = await MapiRulesOperations.ReadRulesAsync(session, folders.InboxFolderId, cancellationToken, Diagnostics).ConfigureAwait(false);
        var sequence = before.Count == 0 ? 10u : before.Max(rule => rule.Sequence) + 1;
        var rule = ExchangeServerRule.FromMailFilter(filter, remoteId: null, sequence: (int)sequence);

        await MapiRulesOperations.AddRuleAsync(session, folders.InboxFolderId, ToMapiRule(rule, folders, new Dictionary<string, RestrictionNode>()), cancellationToken, Diagnostics).ConfigureAwait(false);

        // RopModifyRules returns no rule id (MS-OXCROPS 2.2.11.1.2); the added rule is the one id that was not there.
        var knownIds = before.Select(existing => existing.RuleId).ToHashSet();
        var created = (await ReadInboxRulesAsync(session, folders, cancellationToken).ConfigureAwait(false))
            .Where(candidate => !knownIds.Contains(candidate.RuleId))
            .ToList();
        if (created.Count != 1)
            throw new InvalidOperationException($"Exchange added the rule, but {created.Count} new rules were found, so it cannot be identified. Refresh the rules list.");

        var senders = await ResolveRuleSendersAsync(created, cancellationToken).ConfigureAwait(false);
        return ExchangeServerRule.WithLocalMetadata(FromMapiRule(created[0], folders, senders).ToMailFilter(folders.DeletedItemsRemoteId), filter);
    }

    public override async Task<MailFilter> UpdateProviderFilterAsync(MailFilter filter, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        var folders = await GetRuleFoldersAsync(session).ConfigureAwait(false);
        var (current, currentSenders) = await RequireEditableMapiRuleAsync(session, folders, filter.RemoteId, cancellationToken).ConfigureAwait(false);

        var rule = ExchangeServerRule.FromMailFilter(filter, filter.RemoteId, (int)current.Sequence);
        var storedFromTerms = StoredDirectorySenderTerms(current, currentSenders);
        await MapiRulesOperations.UpdateRuleAsync(session, folders.InboxFolderId, current, ToMapiRule(rule, folders, storedFromTerms), cancellationToken, Diagnostics).ConfigureAwait(false);

        var updated = (await ReadInboxRulesAsync(session, folders, cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.RuleId == current.RuleId)
            ?? throw new InvalidOperationException("Exchange no longer lists the updated rule.");

        var senders = await ResolveRuleSendersAsync([updated], cancellationToken).ConfigureAwait(false);
        return ExchangeServerRule.WithLocalMetadata(FromMapiRule(updated, folders, senders).ToMailFilter(folders.DeletedItemsRemoteId), filter);
    }

    public override async Task DeleteProviderFilterAsync(string remoteId, CancellationToken cancellationToken = default)
    {
        await using var lease = await AcquireSessionAsync(cancellationToken).ConfigureAwait(false);
        var session = lease.Session;
        var folders = await GetRuleFoldersAsync(session).ConfigureAwait(false);
        var (current, _) = await RequireEditableMapiRuleAsync(session, folders, remoteId, cancellationToken).ConfigureAwait(false);
        await MapiRulesOperations.DeleteRuleAsync(session, folders.InboxFolderId, current.RuleId, cancellationToken, Diagnostics).ConfigureAwait(false);
    }

    private async Task<(MapiRuleInfo Rule, Dictionary<string, string> DirectorySenders)> RequireEditableMapiRuleAsync(MapiSession session, RuleFolders folders, string? remoteId, CancellationToken cancellationToken)
    {
        if (!ExchangeServerRule.TryParseRemoteId(remoteId, out var ruleId))
            throw new InvalidOperationException("The Exchange rule has no valid remote identifier.");

        var current = (await ReadInboxRulesAsync(session, folders, cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.RuleId == ruleId)
            ?? throw new InvalidOperationException("The Exchange rule no longer exists on the server. Refresh the rules list.");

        var senders = await ResolveRuleSendersAsync([current], cancellationToken).ConfigureAwait(false);
        if (!FromMapiRule(current, folders, senders).IsEditable)
            throw new InvalidOperationException("The Exchange rule has conditions or actions Wino cannot represent, or belongs to another rule provider, so Wino leaves it unchanged.");

        return (current, senders);
    }

    /// <summary>The rules the user manages as Inbox rules. Rules of other providers (Out of Office, delegates) are not listed.</summary>
    private async Task<List<MapiRuleInfo>> ReadInboxRulesAsync(MapiSession session, RuleFolders folders, CancellationToken cancellationToken)
        => (await MapiRulesOperations.ReadRulesAsync(session, folders.InboxFolderId, cancellationToken, Diagnostics).ConfigureAwait(false))
            .Where(rule => rule.Provider is PropertyTags.RuleOrganizerProvider or ExchangeMailboxRulesProvider)
            .ToList();

    private async Task<RuleFolders> GetRuleFoldersAsync(MapiSession session)
    {
        var logon = session.Logon ?? throw new InvalidOperationException("The MAPI session has no logon.");
        var byEntryId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(folder.MapiEntryId) && !string.IsNullOrEmpty(folder.RemoteFolderId))
                byEntryId.TryAdd(folder.MapiEntryId, folder.RemoteFolderId);
        }

        return new RuleFolders(logon.InboxFolderId, logon.DeletedItemsFolderId, ToMapiId(logon.DeletedItemsFolderId), byEntryId);
    }

    private static ExchangeServerRule FromMapiRule(MapiRuleInfo info, RuleFolders folders, IReadOnlyDictionary<string, string> directorySenders)
    {
        var rule = new ExchangeServerRule
        {
            RemoteId = ExchangeServerRule.ToRemoteId(info.RuleId),
            Name = info.Name,
            IsEnabled = info.IsEnabled,
            Sequence = (int)info.Sequence,
            StopProcessing = info.StopsProcessing,
            HasError = info.HasError,
        };

        // Both parts are always read, so a rule that is not representable still shows what was understood of it.
        var conditionRead = info.Condition is { } condition && ReadCondition(condition, rule, directorySenders);
        var actionsRead = info.Actions is { } actions && ReadActions(actions, rule, folders);
        var representable = conditionRead
            && actionsRead
            && info.Provider is PropertyTags.RuleOrganizerProvider or ExchangeMailboxRulesProvider
            && (info.State & ~EditableStateBits) == 0;

        rule.IsRepresentable = representable;
        return rule;
    }

    /// <summary>
    /// A condition is one predicate group or an AND of groups with no group twice. A group is one term or an OR of
    /// terms of one kind.
    /// </summary>
    private static bool ReadCondition(RestrictionNode condition, ExchangeServerRule rule, IReadOnlyDictionary<string, string> directorySenders)
    {
        var groups = condition.Kind == RestrictionKind.And ? condition.Children : [condition];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var terms = group.Kind == RestrictionKind.Or ? group.Children : [group];
            if (terms.Count == 0)
                return false;

            string? groupKind = null;
            foreach (var term in terms)
            {
                var kind = ReadTerm(term, rule, allowSingleOnly: terms.Count == 1, directorySenders);
                if (kind == null || (groupKind != null && kind != groupKind))
                    return false;
                groupKind = kind;
            }

            if (groupKind == null || !seen.Add(groupKind))
                return false;
        }

        return seen.Count > 0;
    }

    /// <summary>Adds one term to the rule and names its kind, or returns null for a term Wino cannot represent.</summary>
    private static string? ReadTerm(RestrictionNode term, ExchangeServerRule rule, bool allowSingleOnly, IReadOnlyDictionary<string, string> directorySenders)
    {
        switch (term.Kind)
        {
            case RestrictionKind.Content when IsSubstringIgnoreCase(term) && term.PropertyTag == PropertyTags.Subject && term.Value is string subject:
                rule.SubjectContains.Add(subject);
                return nameof(ExchangeServerRule.SubjectContains);

            case RestrictionKind.Content when IsSubstringIgnoreCase(term) && term.PropertyTag == PropertyTags.Body && term.Value is string body:
                rule.BodyContains.Add(body);
                return nameof(ExchangeServerRule.BodyContains);

            // Sender strings match the sender's search key (MS-OXCPRPT PidTagSearchKey: an upper-case "TYPE:ADDRESS").
            case RestrictionKind.Content when term.FuzzyLevelLow == RestrictionOps.FuzzySubString && term.PropertyTag == PropertyTags.SenderSearchKey && term.Value is byte[] key && IsPrintableAscii(key):
                rule.SenderContains.Add(Encoding.ASCII.GetString(key));
                return nameof(ExchangeServerRule.SenderContains);

            case RestrictionKind.Comment when ReadFromAddress(term, directorySenders) is { } address:
                rule.FromAddresses.Add(address);
                return nameof(ExchangeServerRule.FromAddresses);

            case RestrictionKind.Property when allowSingleOnly && term.RelOp == RestrictionOps.RelOpEq && term.PropertyTag == PropertyTags.Importance && term.Value is uint importance && importance < 3:
                rule.Importance = ExchangeServerRule.ImportanceNames[importance];
                return nameof(ExchangeServerRule.Importance);

            case RestrictionKind.BitMask when allowSingleOnly && term.RelOp == RestrictionOps.BitMaskNeZ && term.PropertyTag == PropertyTags.MessageFlags && term.Mask == MessageFlagHasAttachment:
                rule.HasAttachments = true;
                return nameof(ExchangeServerRule.HasAttachments);

            default:
                return null;
        }
    }

    private static bool IsSubstringIgnoreCase(RestrictionNode node)
        => node.FuzzyLevelLow == RestrictionOps.FuzzySubString && node.FuzzyLevelHigh == RestrictionOps.FuzzyIgnoreCase;

    private static bool IsPrintableAscii(byte[] value) => value.Length > 0 && value.All(b => b is >= 0x20 and < 0x7F);

    /// <summary>
    /// A From predicate as Exchange stores it: a RES_COMMENT carrying the sender's entry id and name around
    /// PidTagSenderSearchKey RELOP_EQ "TYPE:ADDRESS\0". An external sender is a one-off entry with "SMTP:ADDRESS"; a
    /// directory sender is an Address Book EntryID with "EX:" and its legacy DN, shown as the primary SMTP address
    /// the address book holds for that DN (<paramref name="directorySenders"/>). A DN the address book does not
    /// resolve leaves the term unrepresentable.
    /// </summary>
    private static string? ReadFromAddress(RestrictionNode comment, IReadOnlyDictionary<string, string> directorySenders)
    {
        if (ReadSenderSearchKey(comment) is not { } text)
            return null;

        if (text.StartsWith(DirectorySearchKeyPrefix, StringComparison.Ordinal))
            return directorySenders.TryGetValue(text[DirectorySearchKeyPrefix.Length..], out var smtp) ? smtp : null;

        if (!text.StartsWith(SmtpSearchKeyPrefix, StringComparison.Ordinal) || text.Length == SmtpSearchKeyPrefix.Length)
            return null;

        var address = text[SmtpSearchKeyPrefix.Length..];
        var commented = comment.CommentValues.FirstOrDefault(value => value.Tag == FromCommentAddressTag)?.Value as string;
        return commented != null && commented.Equals(address, StringComparison.OrdinalIgnoreCase) ? commented : address.ToLowerInvariant();
    }

    private static bool ReadActions(List<RuleAction> actions, ExchangeServerRule rule, RuleFolders folders)
    {
        if (actions.Count == 0)
            return false;

        foreach (var action in actions)
        {
            if (action.Flavor != 0 || action.Flags != 0)
                return false;

            switch (action.Type)
            {
                case RuleActionType.MarkAsRead when !rule.MarkAsRead:
                    rule.MarkAsRead = true;
                    break;

                case RuleActionType.Delete when !rule.PermanentDelete:
                    rule.PermanentDelete = true;
                    break;

                // A move to Deleted Items is what EWS reports as Delete (seen on the lab: the same rule reads Delete).
                case RuleActionType.Move when action.ServerFolderId == folders.DeletedItemsFolderId && !rule.SoftDelete:
                    rule.SoftDelete = true;
                    break;

                case RuleActionType.Move when rule.MoveToFolderRemoteId == null && ResolveMoveTarget(action, folders) is { } target:
                    rule.MoveToFolderRemoteId = target;
                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    private static string? ResolveMoveTarget(RuleAction action, RuleFolders folders)
    {
        if (action.ServerFolderId is { } folderId)
            return ToMapiId(folderId);

        // Outlook can name the folder by its entry id (MS-OXCDATA 2.2.4.1) instead of a ServerEid.
        return action.FolderInThisStore && action.FolderEntryId is { Length: > 0 } entryId
            && folders.RemoteIdByEntryIdHex.TryGetValue(Convert.ToHexString(entryId), out var remoteId)
            ? remoteId
            : null;
    }

    private const uint FromCommentAddressTag = 0x0001001F;
    private const uint FromCommentEntryIdTag = 0x00010102;
    private const uint FromCommentDisplayTypeTag = 0x39000003;

    /// <param name="storedFromTerms">The rule's stored From terms on directory senders, by SMTP address, sent back as they are.</param>
    private static MapiRuleDefinition ToMapiRule(ExchangeServerRule rule, RuleFolders folders, IReadOnlyDictionary<string, RestrictionNode> storedFromTerms)
    {
        var groups = new List<RestrictionNode>();
        AddGroup(groups, rule.FromAddresses.Select(address => storedFromTerms.TryGetValue(address, out var stored) ? stored : FromAddressTerm(address)));
        AddGroup(groups, rule.SubjectContains.Select(value => RestrictionNode.Content(PropertyTags.Subject, value)));
        AddGroup(groups, rule.BodyContains.Select(value => RestrictionNode.Content(PropertyTags.Body, value)));
        if (rule.Importance != null)
            groups.Add(RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.Importance, (uint)Array.IndexOf(ExchangeServerRule.ImportanceNames, rule.Importance)));
        if (rule.HasAttachments)
            groups.Add(RestrictionNode.BitMask(RestrictionOps.BitMaskNeZ, PropertyTags.MessageFlags, MessageFlagHasAttachment));
        AddGroup(groups, rule.SenderContains.Select(SenderContainsTerm));

        var condition = groups.Count == 1 ? groups[0] : RestrictionNode.And([.. groups]);

        var actions = new List<RuleAction>();
        if (rule.MarkAsRead)
            actions.Add(RuleAction.MarkAsRead());
        if (rule.MoveToFolderRemoteId != null)
            actions.Add(RuleAction.MoveTo(TryParseMapiId(rule.MoveToFolderRemoteId, out var folderId)
                ? folderId
                : throw new NotSupportedException("The rule's target folder was synchronized over another transport; synchronize the folders first.")));
        if (rule.SoftDelete)
            actions.Add(RuleAction.MoveTo(folders.DeletedItemsFolderId));
        if (rule.PermanentDelete)
            actions.Add(RuleAction.Delete());

        return new MapiRuleDefinition(rule.Name, (uint)rule.Sequence, rule.IsEnabled, rule.StopProcessing, condition, actions);
    }

    private static void AddGroup(List<RestrictionNode> groups, IEnumerable<RestrictionNode> terms)
    {
        var list = terms.ToList();
        if (list.Count == 1)
            groups.Add(list[0]);
        else if (list.Count > 1)
            groups.Add(RestrictionNode.Or([.. list]));
    }

    private static RestrictionNode SenderContainsTerm(string value)
    {
        var upper = value.ToUpperInvariant();
        if (upper.Any(c => c is < (char)0x20 or > (char)0x7E))
            throw new NotSupportedException("Over MAPI a sender condition matches the sender's address, which holds only ASCII characters.");

        return RestrictionNode.Content(PropertyTags.SenderSearchKey, Encoding.ASCII.GetBytes(upper), RestrictionOps.FuzzySubString, 0);
    }

    private static RestrictionNode FromAddressTerm(string address)
    {
        if (address.Any(c => c > 0x7E || c < 0x20))
            throw new NotSupportedException("A From condition needs an SMTP address.");

        return RestrictionNode.Comment(
        [
            TaggedPropertyValue.Long(PropertyTags.RuleCommentType, 1),
            TaggedPropertyValue.Binary(FromCommentEntryIdTag, RuleActions.OneOffEntryId(address, address)),
            TaggedPropertyValue.Unicode(FromCommentAddressTag, address),
            TaggedPropertyValue.Long(FromCommentDisplayTypeTag, 0),
        ],
        RestrictionNode.Property(RestrictionOps.RelOpEq, PropertyTags.SenderSearchKey, RuleActions.SmtpSearchKey(address)));
    }
}
