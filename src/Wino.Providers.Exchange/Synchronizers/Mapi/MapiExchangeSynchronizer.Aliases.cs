using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Providers.Exchange.MAPI.AddressBook;
using Wino.Providers.Exchange.MAPI.Transport;

namespace Wino.Providers.Exchange.Synchronizers.Mapi;

// The mailbox's SMTP addresses from PidTagAddressBookProxyAddresses of its own address book entry, found by its
// legacyExchangeDN (NSPI DNToMId, then GetProps).
public sealed partial class MapiExchangeSynchronizer
{
    private const string SmtpProxyPrefix = "smtp:";

    protected override async Task SynchronizeAliasesAsync()
    {
        var cancellationToken = CancellationToken.None;
        var credential = await MapiConnectionProbe.ToMapiCredentialAsync(ExchangeAuthenticator, Account, cancellationToken).ConfigureAwait(false);
        var endpoint = await ResolveEndpointAsync(credential, cancellationToken).ConfigureAwait(false);

        var addressBookUrl = endpoint.AddressBookUrl
            ?? throw new InvalidOperationException("Autodiscover advertised no MAPI address book endpoint, so the mailbox's addresses cannot be read.");

        await using var nspi = new NspiClient(new MapiHttpTransport(addressBookUrl, credential, UserAgent, Diagnostics), Diagnostics);
        await nspi.BindAsync(cancellationToken).ConfigureAwait(false);

        var minimalIds = await nspi.DnToMinIdAsync([endpoint.LegacyDn], cancellationToken).ConfigureAwait(false);
        if (minimalIds is not [var minimalId] || minimalId == 0)
            throw new InvalidOperationException("The address book has no entry for the mailbox's distinguished name.");

        var values = await nspi.GetPropsAsync(minimalId, [NspiClient.ProxyAddresses], cancellationToken).ConfigureAwait(false);
        var proxy = values.Find(value => value.Tag == NspiClient.ProxyAddresses);
        if (proxy.Error is { } error)
            throw new InvalidOperationException($"The address book did not return the mailbox's proxy addresses (error 0x{error:X8}).");

        var aliases = new Dictionary<string, RemoteAccountAlias>(StringComparer.OrdinalIgnoreCase);
        var smtpAddresses = (proxy.Value as string[] ?? [])
            .Where(address => address.StartsWith(SmtpProxyPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(address => address[SmtpProxyPrefix.Length..].Trim());

        foreach (var address in smtpAddresses.Prepend(Account.Address))
        {
            if (string.IsNullOrWhiteSpace(address) || aliases.ContainsKey(address))
                continue;

            var isAccountAddress = address.Equals(Account.Address, StringComparison.OrdinalIgnoreCase);
            aliases[address] = new RemoteAccountAlias
            {
                AliasAddress = address,
                ReplyToAddress = address,
                IsPrimary = isAccountAddress,
                IsRootAlias = isAccountAddress,
                IsVerified = true,
                Source = AliasSource.ProviderDiscovered,
                SendCapability = AliasSendCapability.Confirmed
            };
        }

        Logger.Information("MAPI aliases {Account}: {Count} SMTP addresses.", Account.Address, aliases.Count);
        await ExchangeChangeProcessor.UpdateRemoteAliasInformationAsync(Account, aliases.Values.ToList()).ConfigureAwait(false);
    }
}
