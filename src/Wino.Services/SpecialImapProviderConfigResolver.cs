using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Services;

public class SpecialImapProviderConfigResolver(IKnownImapProviderCatalog catalog) : ISpecialImapProviderConfigResolver
{
    public CustomServerInformation GetServerInformation(MailAccount account, AccountCreationDialogResult dialogResult)
    {
        var details = dialogResult.SpecialImapProviderDetails;
        var provider = catalog.GetBySpecialProvider(details.SpecialImapProvider)
            ?? throw new System.InvalidOperationException($"No known IMAP provider configuration exists for '{details.SpecialImapProvider}'.");

        var resolvedConfig = new CustomServerInformation
        {
            IncomingServer = provider.Incoming.Host,
            IncomingServerPort = provider.Incoming.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IncomingServerType = CustomIncomingServerType.IMAP4,
            IncomingServerSocketOption = provider.Incoming.Security,
            IncomingAuthenticationMethod = provider.Incoming.Authentication,
            IncomingServerUsername = catalog.ResolveUsername(provider.Incoming.UsernamePolicy, details.Address),
            OutgoingServer = provider.Outgoing.Host,
            OutgoingServerPort = provider.Outgoing.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OutgoingServerSocketOption = provider.Outgoing.Security,
            OutgoingAuthenticationMethod = provider.Outgoing.Authentication,
            OutgoingServerUsername = catalog.ResolveUsername(provider.Outgoing.UsernamePolicy, details.Address),
            MaxConcurrentClients = provider.MaxConcurrentClients,
            ConnectionPolicyVersion = provider.ConnectionPolicyVersion,
            CalDavServiceUrl = provider.CalDavServiceUrl,
            CardDavServiceUrl = provider.CardDavServiceUrl
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
}
