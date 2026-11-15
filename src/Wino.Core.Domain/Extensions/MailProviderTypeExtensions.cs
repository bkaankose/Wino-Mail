using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Extensions;

public static class MailProviderTypeExtensions
{
    /// <summary>
    /// Accounts whose server settings and credentials live in <see cref="Entities.Shared.CustomServerInformation"/>.
    /// Exchange counts: its server URL, transport and password are stored there.
    /// </summary>
    public static bool IsCustomMailProvider(this MailProviderType providerType)
        => providerType is MailProviderType.IMAP4 or MailProviderType.POP3 or MailProviderType.Exchange;

    public static bool SupportsRemoteFolderSynchronization(this MailProviderType providerType)
        => providerType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4 or MailProviderType.Exchange;

    public static bool SupportsPushSynchronization(this MailProviderType providerType)
        => providerType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4;

    public static bool UsesLocalMailState(this MailProviderType providerType)
        => providerType == MailProviderType.POP3;
}
