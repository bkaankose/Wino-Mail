using Wino.Mapi.Restrictions;
using Wino.Mapi.Rops;

namespace Wino.Mapi.Rules;

/// <summary>
/// Edits the junk rule's condition the way Exchange's own junk settings do, so OWA reads it back
/// unchanged. The shape, captured from a live rule after OWA added one blocked and one safe sender:
///
///   AND
///     OR                        (blocked branch)
///       OR                      (blocked senders: CONTENT PidTagSenderEmailAddress FULLSTRING|IGNORECASE)
///       AND ...                 (the spam-confidence test)
///     NOT
///       OR                      (safe branch)
///         OR                    (safe senders: CONTENT PidTagSenderEmailAddress)
///         SUBOBJECT PidTagMessageRecipients
///           OR                  (safe recipients: CONTENT PidTagEmailAddress)
///         OR
///
/// Anything else is left untouched; a rule that does not have this shape is refused rather than guessed at.
/// </summary>
public static class JunkRuleEditor
{
    /// <summary>The lists as the editor sees them inside the tree.</summary>
    public sealed record JunkRuleLists(RestrictionNode BlockedSenders, RestrictionNode SafeSenders, RestrictionNode SafeRecipients);

    public static JunkRuleLists Locate(RestrictionNode root)
    {
        if (root.Kind != RestrictionKind.And || root.Children.Count < 2)
            throw new MapiFormatException("The junk rule condition is not the AND(blocked, NOT(safe)) shape this client edits.");

        var blockedBranch = root.Children[0];
        if (blockedBranch.Kind != RestrictionKind.Or || blockedBranch.Children.Count < 1 || blockedBranch.Children[0].Kind != RestrictionKind.Or)
            throw new MapiFormatException("The junk rule's blocked branch is not OR(OR(senders), ...).");

        var safeNot = root.Children[1];
        if (safeNot.Kind != RestrictionKind.Not || safeNot.Children.Count != 1 || safeNot.Children[0].Kind != RestrictionKind.Or)
            throw new MapiFormatException("The junk rule's safe branch is not NOT(OR(...)).");
        var safeOr = safeNot.Children[0];
        if (safeOr.Children.Count < 2 || safeOr.Children[0].Kind != RestrictionKind.Or)
            throw new MapiFormatException("The junk rule's safe branch has no senders list.");
        var recipients = safeOr.Children[1];
        if (recipients.Kind != RestrictionKind.SubObject || recipients.PropertyTag != PropertyTags.MessageRecipients || recipients.Children.Count != 1 || recipients.Children[0].Kind != RestrictionKind.Or)
            throw new MapiFormatException("The junk rule's safe branch has no recipients subobject.");

        return new JunkRuleLists(blockedBranch.Children[0], safeOr.Children[0], recipients.Children[0]);
    }

    /// <summary>Adds the sender to Blocked Senders (once) and drops it from the safe lists, as marking junk does.</summary>
    public static RestrictionNode Block(RestrictionNode root, string address)
    {
        var normalized = Normalize(address);
        var lists = Locate(root);
        Remove(lists.SafeSenders, normalized);
        Remove(lists.SafeRecipients, normalized);
        if (!Contains(lists.BlockedSenders, normalized))
            lists.BlockedSenders.Children.Add(Term(PropertyTags.SenderEmailAddress, normalized));
        return root;
    }

    /// <summary>
    /// Adds the address to the safe lists the way OWA's "Block or allow" page does: to safe senders
    /// and, under the recipients subobject, to safe recipients; and drops it from Blocked Senders.
    /// </summary>
    public static RestrictionNode Trust(RestrictionNode root, string address)
    {
        var normalized = Normalize(address);
        var lists = Locate(root);
        Remove(lists.BlockedSenders, normalized);
        if (!Contains(lists.SafeSenders, normalized))
            lists.SafeSenders.Children.Add(Term(PropertyTags.SenderEmailAddress, normalized));
        if (!Contains(lists.SafeRecipients, normalized))
            lists.SafeRecipients.Children.Add(Term(PropertyTags.EmailAddress, normalized));
        return root;
    }

    /// <summary>Drops the address from both safe lists.</summary>
    public static RestrictionNode Untrust(RestrictionNode root, string address)
    {
        var normalized = Normalize(address);
        var lists = Locate(root);
        Remove(lists.SafeSenders, normalized);
        Remove(lists.SafeRecipients, normalized);
        return root;
    }

    /// <summary>Drops the sender from Blocked Senders, as marking not-junk does.</summary>
    public static RestrictionNode Unblock(RestrictionNode root, string address)
    {
        Remove(Locate(root).BlockedSenders, Normalize(address));
        return root;
    }

    /// <summary>An address term as Exchange writes one: full-string, case-insensitive, on PidTagSenderEmailAddress.</summary>
    public static RestrictionNode Term(uint propertyTag, string address)
        => RestrictionNode.Content(propertyTag, address, RestrictionOps.FuzzyFullString, RestrictionOps.FuzzyIgnoreCase);

    public static string Normalize(string address) => address.Trim().ToLowerInvariant();

    /// <summary>An address term of a list naming this address, case-insensitively and ignoring the stored term's padding.</summary>
    private static bool IsTermFor(RestrictionNode node, string address)
        => node.Kind == RestrictionKind.Content && node.Value is string v && string.Equals(v.Trim(), address, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(RestrictionNode list, string address) => list.Children.Any(c => IsTermFor(c, address));

    private static void Remove(RestrictionNode list, string address) => list.Children.RemoveAll(c => IsTermFor(c, address));
}
