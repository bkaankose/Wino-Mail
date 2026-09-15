using Wino.Mapi.Restrictions;
using Wino.Mapi.Rops;
using Wino.Mapi.Wire;

namespace Wino.Mapi.Rules;

/// <summary>One row of a folder's rules table (MS-OXORULE 2.2.1.3), decoded.</summary>
public sealed record MapiRuleInfo(
    ulong RuleId,
    uint Sequence,
    uint State,
    string Name,
    string Provider,
    uint Level,
    uint UserFlags,
    byte[]? ProviderData,
    RestrictionNode? Condition,
    List<RuleAction>? Actions)
{
    public const uint StateEnabled = 0x00000001;
    public const uint StateError = 0x00000002;
    public const uint StateOnlyWhenOof = 0x00000004;
    public const uint StateExitLevel = 0x00000010;

    public bool IsEnabled => (State & StateEnabled) != 0;
    public bool HasError => (State & StateError) != 0;
    public bool StopsProcessing => (State & StateExitLevel) != 0;
}

/// <summary>A rule as this client writes it: the subset of the row that RopModifyRules needs.</summary>
public sealed record MapiRuleDefinition(
    string Name,
    uint Sequence,
    bool IsEnabled,
    bool StopProcessing,
    RestrictionNode Condition,
    IReadOnlyList<RuleAction> Actions)
{
    public uint State => (IsEnabled ? MapiRuleInfo.StateEnabled : 0) | (StopProcessing ? MapiRuleInfo.StateExitLevel : 0);
}

/// <summary>One row of the Inbox's associated-contents (FAI) table that concerns rules.</summary>
public sealed record MapiFaiRuleRow(string MessageClass, string? Provider, string? Name, ulong MessageId);

/// <summary>The junk rule's lists, read from PidTagExtendedRuleMessageCondition.</summary>
public sealed record MapiJunkLists(List<string> BlockedSenders, List<string> SafeSenders, List<string> SafeRecipients, RestrictionNode Condition, ulong MessageId = 0);

/// <summary>
/// Rules over the rules table (rung 8). This is the classic-rule surface, the one EWS also reaches;
/// the point of doing it here is that the condition and actions come back as structures rather than
/// EWS's lossy predicate model, and the junk rule (an extended rule, read from the FAI table) sits
/// beside them.
/// </summary>
public static class MapiRulesOperations
{
    private const int Slots = 5;

    public static async Task<List<MapiRuleInfo>> ReadRulesAsync(MapiSession session, ulong folderId, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var handles = await OpenFolderAsync(session, folderId, cancellationToken).ConfigureAwait(false);

        try
        {
            var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildGetRulesTable(1, 2), handles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseGetRulesTable(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, PropertyTags.RulesTableColumns), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));

                var rules = new List<MapiRuleInfo>();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Rules can be large (a condition with hundreds of terms); ask for few rows per page.
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 10), handles, cancellationToken).ConfigureAwait(false);
                    List<PropertyValue[]> rows;
                    try
                    {
                        rows = RopFolder.ParseQueryRows(new RopReader(rops), PropertyTags.RulesTableColumns);
                    }
                    catch (MapiFormatException)
                    {
                        diagnostics?.Invoke("rules table QueryRows response could not be parsed:" + "\n" + HexDump.Render(rops, 2048));
                        throw;
                    }

                    diagnostics?.Invoke($"rules table QueryRows: {rows.Count} rows, {rops.Length} bytes" + "\n" + HexDump.Render(rops, 1024));
                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        if (row[0].AsUInt64 is not { } ruleId)
                        {
                            diagnostics?.Invoke("rules table row without PidTagRuleId: " + string.Join(", ", row.Select(c => $"{PropertyTags.Describe(c.Tag)}={(c.Error is { } e ? $"err 0x{e:X8}" : c.Value is null ? "absent" : "ok")}")));
                            continue;
                        }

                        rules.Add(new MapiRuleInfo(
                            ruleId,
                            row[1].AsUInt32 ?? 0,
                            row[2].AsUInt32 ?? 0,
                            row[3].AsString ?? string.Empty,
                            row[4].AsString ?? string.Empty,
                            row[5].AsUInt32 ?? 0,
                            row[6].AsUInt32 ?? 0,
                            row[7].AsBinary,
                            row[8].Value as RestrictionNode,
                            row[9].Value as List<RuleAction>));
                    }
                }

                diagnostics?.Invoke($"rules table of 0x{folderId:X16}: {rules.Count} rules ({rules.Count(r => r.IsEnabled)} enabled, providers: {string.Join(",", rules.Select(r => r.Provider).Distinct())})");
                return rules;
            }
            finally
            {
                await session.ReleaseAsync(handles[2], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Adds a rule. The server assigns the rule id; read the table again to learn it.</summary>
    public static Task AddRuleAsync(MapiSession session, ulong folderId, MapiRuleDefinition rule, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
        => ModifyRulesAsync(session, folderId, [new RopFolder.RuleData(RopFolder.RuleDataFlags.Add, RuleProperties(rule, ruleId: null))], cancellationToken, diagnostics);

    /// <summary>Rewrites every property of an existing rule.</summary>
    public static Task UpdateRuleAsync(MapiSession session, ulong folderId, ulong ruleId, MapiRuleDefinition rule, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
        => ModifyRulesAsync(session, folderId, [new RopFolder.RuleData(RopFolder.RuleDataFlags.Modify, RuleProperties(rule, ruleId))], cancellationToken, diagnostics);

    /// <summary>Changes only sequence, state or name; condition and actions are left as stored.</summary>
    public static Task UpdateRuleHeaderAsync(MapiSession session, ulong folderId, ulong ruleId, uint sequence, uint state, string name, CancellationToken cancellationToken = default)
        => ModifyRulesAsync(session, folderId,
        [
            new RopFolder.RuleData(RopFolder.RuleDataFlags.Modify,
            [
                new TaggedPropertyValue(PropertyTags.RuleId, ruleId),
                TaggedPropertyValue.Long(PropertyTags.RuleSequence, sequence),
                TaggedPropertyValue.Long(PropertyTags.RuleState, state),
                TaggedPropertyValue.Unicode(PropertyTags.RuleName, name),
            ]),
        ], cancellationToken);

    public static Task DeleteRuleAsync(MapiSession session, ulong folderId, ulong ruleId, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
        => ModifyRulesAsync(session, folderId, [new RopFolder.RuleData(RopFolder.RuleDataFlags.Remove, [new TaggedPropertyValue(PropertyTags.RuleId, ruleId)])], cancellationToken, diagnostics);

    /// <summary>Replaces the folder's whole rules table with nothing (ModifyRulesFlags ReplaceAll). Recovery only.</summary>
    public static Task ClearAllRulesAsync(MapiSession session, ulong folderId, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
        => ModifyRulesAsync(session, folderId, [], cancellationToken, diagnostics, replaceAll: true);

    public static async Task ModifyRulesAsync(MapiSession session, ulong folderId, IReadOnlyList<RopFolder.RuleData> rules, CancellationToken cancellationToken = default, Action<string>? diagnostics = null, bool replaceAll = false)
    {
        var handles = await OpenFolderAsync(session, folderId, cancellationToken).ConfigureAwait(false);
        try
        {
            var request = RopFolder.BuildModifyRules(1, rules, replaceAll);
            diagnostics?.Invoke($"RopModifyRules request ({rules.Count} rules, replaceAll={replaceAll}):" + "\n" + HexDump.Render(request, 2048));
            var (rops, _) = await session.ExecuteAsync(request, handles, cancellationToken).ConfigureAwait(false);
            diagnostics?.Invoke("RopModifyRules response:" + "\n" + HexDump.Render(rops, 256));
            RopFolder.ParseModifyRules(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<TaggedPropertyValue> RuleProperties(MapiRuleDefinition rule, ulong? ruleId)
    {
        var values = new List<TaggedPropertyValue>();
        if (ruleId is { } id)
            values.Add(new TaggedPropertyValue(PropertyTags.RuleId, id));

        values.Add(TaggedPropertyValue.Long(PropertyTags.RuleSequence, rule.Sequence));
        values.Add(TaggedPropertyValue.Long(PropertyTags.RuleLevel, 0));
        values.Add(TaggedPropertyValue.Long(PropertyTags.RuleState, rule.State));
        values.Add(TaggedPropertyValue.Unicode(PropertyTags.RuleName, rule.Name));
        values.Add(TaggedPropertyValue.Unicode(PropertyTags.RuleProvider, PropertyTags.RuleOrganizerProvider));
        values.Add(new TaggedPropertyValue(PropertyTags.RuleCondition, rule.Condition));
        values.Add(new TaggedPropertyValue(PropertyTags.RuleActions, rule.Actions));
        return values;
    }

    /// <summary>The property tag of PidNameKeywords (categories) in this store.</summary>
    public static async Task<uint> ResolveKeywordsTagAsync(MapiSession session, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], 1);
        var (rops, _) = await session.ExecuteAsync(
            RopProperties.BuildGetPropertyIdsFromNames(0, [(PropertyTags.PublicStringsPropertySet, PropertyTags.KeywordsPropertyName)]),
            handles, cancellationToken).ConfigureAwait(false);
        var ids = RopProperties.ParseGetPropertyIdsFromNames(new RopReader(rops));
        if (ids.Count != 1 || ids[0] == 0)
            throw new MapiFormatException("The store did not map the Keywords named property.");

        return ((uint)ids[0] << 16) | PropertyTypes.MultipleUnicode;
    }

    /// <summary>The rule-related rows of the folder's FAI table: classic rule messages, the junk rule, the Outlook blob.</summary>
    public static async Task<List<MapiFaiRuleRow>> ReadFaiRuleRowsAsync(MapiSession session, ulong folderId, CancellationToken cancellationToken = default)
    {
        var handles = await OpenFolderAsync(session, folderId, cancellationToken).ConfigureAwait(false);
        try
        {
            var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildGetContentsTable(1, 2, RopFolder.TableFlags.Associated), handles, cancellationToken).ConfigureAwait(false);
            RopFolder.ParseGetContentsTable(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                (rops, _) = await session.ExecuteAsync(RopFolder.BuildSetColumns(2, PropertyTags.RuleColumns), handles, cancellationToken).ConfigureAwait(false);
                RopFolder.ParseSetColumns(new RopReader(rops));

                var result = new List<MapiFaiRuleRow>();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (rops, _) = await session.ExecuteAsync(RopFolder.BuildQueryRows(2, 100), handles, cancellationToken).ConfigureAwait(false);
                    var rows = RopFolder.ParseQueryRows(new RopReader(rops), PropertyTags.RuleColumns);
                    if (rows.Count == 0)
                        break;

                    foreach (var row in rows)
                    {
                        if (row[0].AsString is { } messageClass && row[3].AsUInt64 is { } mid)
                            result.Add(new MapiFaiRuleRow(messageClass, row[1].AsString, row[2].AsString, mid));
                    }
                }

                return result;
            }
            finally
            {
                await session.ReleaseAsync(handles[2], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[1], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The classic Outlook rule blob (IPM.RuleOrganizer, PidTagRwRulesStream), if the mailbox has one.
    /// EWS refuses rule updates while it exists; this path can remove it with the user's consent.
    /// </summary>
    public static async Task<ulong?> FindOutlookRuleBlobAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken = default)
    {
        var rows = await ReadFaiRuleRowsAsync(session, inboxFolderId, cancellationToken).ConfigureAwait(false);
        return rows.FirstOrDefault(r => string.Equals(r.MessageClass, PropertyTags.RuleOrganizerMessageClass, StringComparison.OrdinalIgnoreCase))?.MessageId;
    }

    public static Task DeleteFaiMessageAsync(MapiSession session, ulong folderId, ulong messageId, CancellationToken cancellationToken = default)
        => MapiMessageOperations.DeleteAsync(session, folderId, [messageId], cancellationToken);

    /// <summary>
    /// Reads the junk rule's lists. Blocked senders are the sender-address terms of the condition;
    /// safe senders (and safe recipients, under the recipients subobject) are the terms negated by an
    /// enclosing NOT. Null when the mailbox has no junk rule.
    /// </summary>
    public static async Task<MapiJunkLists?> ReadJunkListsAsync(MapiSession session, ulong inboxFolderId, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var rows = await ReadFaiRuleRowsAsync(session, inboxFolderId, cancellationToken).ConfigureAwait(false);
        var junk = rows.FirstOrDefault(r => r.MessageClass == PropertyTags.ExtendedRuleMessageClass && r.Provider == PropertyTags.JunkEmailRuleProvider)
                ?? rows.FirstOrDefault(r => r.MessageClass == PropertyTags.ExtendedRuleMessageClass);
        if (junk is null)
            return null;

        var condition = await ReadPropertyStreamAsync(session, inboxFolderId, junk.MessageId, PropertyTags.ExtendedRuleMessageCondition, cancellationToken).ConfigureAwait(false);
        var lists = ClassifyJunkCondition(Restriction.DecodeExtendedRuleCondition(condition)) with { MessageId = junk.MessageId };
        diagnostics?.Invoke($"junk rule: condition {condition.Length} bytes; blocked {lists.BlockedSenders.Count}, safe {lists.SafeSenders.Count}, safe recipients {lists.SafeRecipients.Count}");
        return lists;
    }

    /// <summary>
    /// Rewrites the junk rule's condition through <paramref name="edit"/> (see <see cref="JunkRuleEditor"/>):
    /// reads the current condition, applies the edit, and streams the re-encoded restriction back onto
    /// the rule message. Returns false when the mailbox has no junk rule.
    /// </summary>
    public static async Task<bool> UpdateJunkRuleAsync(MapiSession session, ulong inboxFolderId, Func<RestrictionNode, RestrictionNode> edit, CancellationToken cancellationToken = default, Action<string>? diagnostics = null)
    {
        var lists = await ReadJunkListsAsync(session, inboxFolderId, cancellationToken, diagnostics).ConfigureAwait(false);
        if (lists is null || lists.MessageId == 0)
            return false;

        var condition = Restriction.EncodeExtendedRuleCondition(edit(lists.Condition));

        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(inboxFolderId, lists.MessageId, 0, 3, RopMessageOps.OpenReadWrite), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);
        try
        {
            await MapiMessageComposer.WritePropertyAsync(session, handles, 3, 4, PropertyTags.ExtendedRuleMessageCondition, condition, isUnicodeString: false, cancellationToken).ConfigureAwait(false);
            (rops, _) = await session.ExecuteAsync(RopMessageOps.BuildSaveChangesMessage(4, 3), handles, cancellationToken).ConfigureAwait(false);
            RopMessageOps.ParseSaveChangesMessage(new RopReader(rops));
        }
        finally
        {
            await session.ReleaseAsync(handles[3], cancellationToken).ConfigureAwait(false);
        }

        var after = ClassifyJunkCondition(Restriction.DecodeExtendedRuleCondition(condition));
        diagnostics?.Invoke($"junk rule rewritten: {condition.Length} bytes; blocked {after.BlockedSenders.Count}, safe {after.SafeSenders.Count}, safe recipients {after.SafeRecipients.Count}");
        return true;
    }

    /// <summary>
    /// Splits the junk rule's condition into its lists. The rule is shaped as "not (safe sender or safe
    /// recipient) and (blocked sender or spam level)": address terms under an odd number of NOTs are
    /// the safe lists, the rest are blocked senders. Values are lower-cased; a term may be a bare
    /// domain, which the local lists accept as-is.
    /// </summary>
    public static MapiJunkLists ClassifyJunkCondition(RestrictionNode node)
    {
        var lists = new MapiJunkLists([], [], [], node);
        Classify(node, negated: false, inRecipients: false, lists);
        return lists;
    }

    private static void Classify(RestrictionNode node, bool negated, bool inRecipients, MapiJunkLists lists)
    {
        switch (node.Kind)
        {
            case RestrictionKind.Not:
                foreach (var child in node.Children) Classify(child, !negated, inRecipients, lists);
                return;

            case RestrictionKind.SubObject:
                foreach (var child in node.Children) Classify(child, negated, inRecipients || node.PropertyTag == PropertyTags.MessageRecipients, lists);
                return;

            case RestrictionKind.Content when node.Value is string address && IsAddressProperty(node.PropertyTag):
                var normalized = address.Trim().ToLowerInvariant();
                if (normalized.Length == 0)
                    return;

                if (inRecipients)
                {
                    if (negated) lists.SafeRecipients.Add(normalized);
                }
                else if (negated)
                {
                    lists.SafeSenders.Add(normalized);
                }
                else
                {
                    lists.BlockedSenders.Add(normalized);
                }
                return;

            default:
                foreach (var child in node.Children) Classify(child, negated, inRecipients, lists);
                return;
        }
    }

    private static bool IsAddressProperty(uint? tag)
        => tag is PropertyTags.SenderEmailAddress or PropertyTags.SenderSmtpAddress or PropertyTags.SentRepresentingSmtpAddress or PropertyTags.EmailAddress or PropertyTags.SmtpAddress;

    /// <summary>Opens a message by id and streams one property in safe-sized chunks.</summary>
    public static async Task<byte[]> ReadPropertyStreamAsync(MapiSession session, ulong folderId, ulong messageId, uint propertyTag, CancellationToken cancellationToken = default)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenMessage(folderId, messageId, 0, 3), handles, cancellationToken).ConfigureAwait(false);
        RopMessage.ParseOpenMessage(new RopReader(rops));
        handles = MapiSession.MergeHandles(handles, returned, Slots);

        try
        {
            (rops, returned) = await session.ExecuteAsync(RopMessage.BuildOpenStream(propertyTag, 3, 4), handles, cancellationToken).ConfigureAwait(false);
            var size = RopMessage.ParseOpenStream(new RopReader(rops));
            handles = MapiSession.MergeHandles(handles, returned, Slots);

            try
            {
                var buffer = new MemoryStream((int)Math.Min(size, 16 * 1024 * 1024));
                while (buffer.Length < size)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var want = Math.Min(RopMessage.SafeReadChunk, size - (uint)buffer.Length);
                    (rops, _) = await session.ExecuteAsync(RopMessage.BuildReadStream(4, want), handles, cancellationToken).ConfigureAwait(false);
                    var data = RopMessage.ParseReadStream(new RopReader(rops));
                    if (data.Length == 0)
                        break;
                    buffer.Write(data);
                }

                return buffer.ToArray();
            }
            finally
            {
                await session.ReleaseAsync(handles[4], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await session.ReleaseAsync(handles[3], cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<uint[]> OpenFolderAsync(MapiSession session, ulong folderId, CancellationToken cancellationToken)
    {
        var handles = MapiSession.MergeHandles([session.LogonHandle], [], Slots);
        var (rops, returned) = await session.ExecuteAsync(RopFolder.BuildOpenFolder(folderId, 0, 1), handles, cancellationToken).ConfigureAwait(false);
        RopFolder.ParseOpenFolder(new RopReader(rops));
        return MapiSession.MergeHandles(handles, returned, Slots);
    }
}
