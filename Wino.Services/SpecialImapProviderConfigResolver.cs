using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Services;

public class SpecialImapProviderConfigResolver : ISpecialImapProviderConfigResolver
{
    public CustomServerInformation GetServerInformation(MailAccount account, AccountCreationDialogResult dialogResult)
    {
        CustomServerInformation resolvedConfig = null;
        var details = dialogResult.SpecialImapProviderDetails;

        if (details.SpecialImapProvider == SpecialImapProvider.iCloud)
        {
            resolvedConfig = new CustomServerInformation()
            {
                IncomingServer = "imap.mail.me.com",
                IncomingServerPort = "993",
                IncomingServerType = CustomIncomingServerType.IMAP4,
                IncomingServerSocketOption = ImapConnectionSecurity.Auto,
                IncomingAuthenticationMethod = ImapAuthenticationMethod.Auto,
                OutgoingServer = "smtp.mail.me.com",
                OutgoingServerPort = "587",
                OutgoingServerSocketOption = ImapConnectionSecurity.Auto,
                OutgoingAuthenticationMethod = ImapAuthenticationMethod.Auto,
                MaxConcurrentClients = 5,
                CalDavServiceUrl = "https://caldav.icloud.com/"
            };

            // iCloud takes username before the @icloud part for incoming, but full address as outgoing.
            resolvedConfig.IncomingServerUsername = details.Address.Split('@')[0];
            resolvedConfig.OutgoingServerUsername = details.Address;
        }
        else if (details.SpecialImapProvider == SpecialImapProvider.Yahoo)
        {
            resolvedConfig = new CustomServerInformation()
            {
                IncomingServer = "imap.mail.yahoo.com",
                IncomingServerPort = "993",
                IncomingServerType = CustomIncomingServerType.IMAP4,
                IncomingServerSocketOption = ImapConnectionSecurity.Auto,
                IncomingAuthenticationMethod = ImapAuthenticationMethod.Auto,
                OutgoingServer = "smtp.mail.yahoo.com",
                OutgoingServerPort = "587",
                OutgoingServerSocketOption = ImapConnectionSecurity.Auto,
                OutgoingAuthenticationMethod = ImapAuthenticationMethod.Auto,
                MaxConcurrentClients = 5,
                CalDavServiceUrl = "https://caldav.calendar.yahoo.com/"
            };

            // Yahoo uses full address for both incoming and outgoing.
            resolvedConfig.IncomingServerUsername = details.Address;
            resolvedConfig.OutgoingServerUsername = details.Address;
        }

        // Fill in account details.
        resolvedConfig.Address = details.Address;
        resolvedConfig.IncomingServerPassword = details.Password;
        resolvedConfig.OutgoingServerPassword = details.Password;
        resolvedConfig.DisplayName = details.SenderName;
        resolvedConfig.CalendarSupportMode = details.CalendarSupportMode;
        resolvedConfig.CalDavUsername = details.Address;
        resolvedConfig.CalDavPassword = details.Password;

        if (details.CalendarSupportMode != ImapCalendarSupportMode.CalDav)
        {
            resolvedConfig.CalDavServiceUrl = string.Empty;
            resolvedConfig.CalDavUsername = string.Empty;
            resolvedConfig.CalDavPassword = string.Empty;
        }

        return resolvedConfig;
    }
}
