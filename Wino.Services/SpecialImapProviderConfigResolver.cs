using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Services;

public class SpecialImapProviderConfigResolver : ISpecialImapProviderConfigResolver
{
    private readonly CustomServerInformation iCloudServerConfig = new CustomServerInformation()
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
    };

    private readonly CustomServerInformation yahooServerConfig = new CustomServerInformation()
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
    };


    public CustomServerInformation GetServerInformation(MailAccount account, AccountCreationDialogResult dialogResult)
    {
        CustomServerInformation resolvedConfig = null;

        if (dialogResult.SpecialImapProviderDetails.SpecialImapProvider == SpecialImapProvider.iCloud)
        {
            resolvedConfig = iCloudServerConfig;

            // iCloud takes username before the @icloud part for incoming, but full address as outgoing.
            resolvedConfig.IncomingServerUsername = dialogResult.SpecialImapProviderDetails.Address.Split('@')[0];
            resolvedConfig.OutgoingServerUsername = dialogResult.SpecialImapProviderDetails.Address;
        }
        else if (dialogResult.SpecialImapProviderDetails.SpecialImapProvider == SpecialImapProvider.Yahoo)
        {
            resolvedConfig = yahooServerConfig;

            // Yahoo uses full address for both incoming and outgoing.
            resolvedConfig.IncomingServerUsername = dialogResult.SpecialImapProviderDetails.Address;
            resolvedConfig.OutgoingServerUsername = dialogResult.SpecialImapProviderDetails.Address;
        }

        // Fill in account details.
        resolvedConfig.Address = dialogResult.SpecialImapProviderDetails.Address;
        resolvedConfig.IncomingServerPassword = dialogResult.SpecialImapProviderDetails.Password;
        resolvedConfig.OutgoingServerPassword = dialogResult.SpecialImapProviderDetails.Password;
        resolvedConfig.DisplayName = dialogResult.SpecialImapProviderDetails.SenderName;
        return resolvedConfig;
    }
}
