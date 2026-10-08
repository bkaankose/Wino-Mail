using System;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Services;

public class SpecialImapProviderConfigResolver(IKnownImapProviderCatalog catalog) : ISpecialImapProviderConfigResolver
{
    public CustomServerInformation GetServerInformation(MailAccount account, AccountCreationDialogResult dialogResult)
    {
        var details = dialogResult.SpecialImapProviderDetails;
        var provider = catalog.GetBySpecialProvider(details.SpecialImapProvider)
            ?? throw new System.InvalidOperationException($"No known IMAP provider configuration exists for '{details.SpecialImapProvider}'.");

        // A region only swaps hosts and DAV endpoints; the first one is the default when none was chosen.
        var region = ResolveRegion(provider, details.RegionId);

        var resolvedConfig = new CustomServerInformation
        {
            IncomingServer = region?.IncomingHost ?? provider.Incoming.Host,
            IncomingServerPort = provider.Incoming.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IncomingServerType = CustomIncomingServerType.IMAP4,
            IncomingServerSocketOption = provider.Incoming.Security,
            IncomingAuthenticationMethod = provider.Incoming.Authentication,
            IncomingServerUsername = catalog.ResolveUsername(provider.Incoming.UsernamePolicy, details.Address),
            OutgoingServer = region?.OutgoingHost ?? provider.Outgoing.Host,
            OutgoingServerPort = provider.Outgoing.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OutgoingServerSocketOption = provider.Outgoing.Security,
            OutgoingAuthenticationMethod = provider.Outgoing.Authentication,
            OutgoingServerUsername = catalog.ResolveUsername(provider.Outgoing.UsernamePolicy, details.Address),
            MaxConcurrentClients = provider.MaxConcurrentClients,
            ConnectionPolicyVersion = provider.ConnectionPolicyVersion,
            CalDavServiceUrl = region == null ? provider.CalDavServiceUrl : region.CalDavServiceUrl,
            CardDavServiceUrl = region == null ? provider.CardDavServiceUrl : region.CardDavServiceUrl
        };

        // Fill in account details.
        resolvedConfig.Address = details.Address;
        resolvedConfig.IncomingServerPassword = details.Password;
        resolvedConfig.OutgoingServerPassword = details.Password;
        resolvedConfig.DisplayName = details.SenderName;
        resolvedConfig.CalendarSupportMode = details.CalendarSupportMode;
        resolvedConfig.CalDavUsername = details.Address;
        resolvedConfig.CalDavPassword = details.Password;

        var requiresDavCredentials = details.CalendarSupportMode == ImapCalendarSupportMode.CalDav ||
            account.IsContactAccessGranted && account.ContactIntegrationSource == AccountIntegrationSource.Dav;

        if (details.CalendarSupportMode != ImapCalendarSupportMode.CalDav)
        {
            resolvedConfig.CalDavServiceUrl = string.Empty;
        }

        if (!requiresDavCredentials)
        {
            resolvedConfig.CalDavUsername = string.Empty;
            resolvedConfig.CalDavPassword = string.Empty;
        }

        return resolvedConfig;
    }

    private static KnownImapProviderRegion ResolveRegion(KnownImapProviderDefinition provider, string regionId)
    {
        if (provider.Regions == null || provider.Regions.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(regionId))
        {
            var match = provider.Regions.FirstOrDefault(region =>
                string.Equals(region.Id, regionId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;
        }

        return provider.Regions[0];
    }
}
