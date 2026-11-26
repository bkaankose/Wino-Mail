using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Providers.Exchange.MAPI;
using Wino.Providers.Exchange.MAPI.AddressBook;
using Wino.Providers.Exchange.MAPI.Restrictions;
using Wino.Providers.Exchange.MAPI.Rops;
using Wino.Providers.Exchange.MAPI.Rules;
using Wino.Providers.Exchange.MAPI.Transport;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

// Rule From predicates that name an address book entry. Exchange stores a sender it finds in the directory as that
// entry: an Address Book EntryID (MS-OXCDATA 2.2.5.2) in the comment and PidTagSenderSearchKey "EX:<legacy DN>".
// Reading resolves the DN to the entry's primary SMTP address (NSPI DNToMId, MS-OXCMAPIHTTP 2.2.5.4, then GetProps
// PidTagSmtpAddress, 2.2.5.7). Writing sends such a sender's stored term back unchanged while the filter still names
// its address; an address the filter adds is written as SMTP, the shape EWS stores for an SMTP From address.
public sealed partial class MapiExchangeSynchronizer
{
    private const string DirectorySearchKeyPrefix = "EX:";

    /// <summary>The upper-case "TYPE:ADDRESS" a From comment matches on, or null when the term is not that shape.</summary>
    private static string? ReadSenderSearchKey(RestrictionNode comment)
    {
        if (comment.Children is not [{ Kind: RestrictionKind.Property, RelOp: RestrictionOps.RelOpEq } inner]
            || inner.PropertyTag != PropertyTags.SenderSearchKey
            || inner.Value is not byte[] key
            || key.Length < 2
            || key[^1] != 0
            || !IsPrintableAscii(key[..^1]))
        {
            return null;
        }

        return Encoding.ASCII.GetString(key, 0, key.Length - 1);
    }

    private static IEnumerable<RestrictionNode> FromComments(MapiRuleInfo rule)
        => rule.Condition is { } condition ? Descendants(condition).Where(node => node.Kind == RestrictionKind.Comment) : Enumerable.Empty<RestrictionNode>();

    private static IEnumerable<RestrictionNode> Descendants(RestrictionNode node)
        => node.Children.SelectMany(Descendants).Prepend(node);

    private static string? DirectoryDistinguishedName(RestrictionNode comment)
        => ReadSenderSearchKey(comment) is { } key && key.StartsWith(DirectorySearchKeyPrefix, StringComparison.Ordinal) && key.Length > DirectorySearchKeyPrefix.Length
            ? key[DirectorySearchKeyPrefix.Length..]
            : null;

    /// <summary>
    /// Legacy DN to primary SMTP address for every directory sender the rules' From predicates name. A DN the address
    /// book does not know (a deleted mailbox, a foreign X500 address) is left out, which keeps its rule read-only.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveRuleSendersAsync(IEnumerable<MapiRuleInfo> rules, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var distinguishedNames = rules
            .SelectMany(FromComments)
            .Select(DirectoryDistinguishedName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinguishedNames.Count == 0)
            return resolved;

        var credential = await MapiConnectionProbe.ToMapiCredentialAsync(ExchangeAuthenticator, Account, cancellationToken).ConfigureAwait(false);
        var endpoint = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);
        if (endpoint.AddressBookUrl is not { } addressBookUrl)
        {
            Logger.Information("MAPI rules {Account}: Autodiscover advertised no address book endpoint; rules on directory senders stay read-only.", Account.Address);
            return resolved;
        }

        await using var nspi = new NspiClient(new MapiHttpTransport(addressBookUrl, credential, UserAgent, Diagnostics), Diagnostics);
        await nspi.BindAsync(cancellationToken).ConfigureAwait(false);

        // DNToMId answers one Minimal Entry ID per DN, 0 for a DN it does not know (MS-OXNSPI 3.1.4.1.13).
        var minimalIds = await nspi.DnToMinIdAsync(distinguishedNames, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < distinguishedNames.Count && i < minimalIds.Length; i++)
        {
            if (minimalIds[i] == 0)
                continue;

            var values = await nspi.GetPropsAsync(minimalIds[i], [PropertyTags.SmtpAddress], cancellationToken).ConfigureAwait(false);
            if (values.Find(value => value.Tag == PropertyTags.SmtpAddress).Value is string { Length: > 0 } smtp)
                resolved[distinguishedNames[i]] = smtp;
        }

        return resolved;
    }

    /// <summary>The stored From term of each directory sender of <paramref name="rule"/>, by the SMTP address Wino shows for it.</summary>
    private static Dictionary<string, RestrictionNode> StoredDirectorySenderTerms(MapiRuleInfo rule, IReadOnlyDictionary<string, string> directorySenders)
    {
        var terms = new Dictionary<string, RestrictionNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var comment in FromComments(rule))
        {
            if (DirectoryDistinguishedName(comment) is { } distinguishedName && directorySenders.TryGetValue(distinguishedName, out var smtp))
                terms.TryAdd(smtp, comment);
        }

        return terms;
    }
}
