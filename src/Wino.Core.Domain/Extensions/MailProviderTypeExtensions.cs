using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Extensions;

public static class MailProviderTypeExtensions
{
    public static bool IsCustomMailProvider(this MailProviderType providerType)
        => providerType is MailProviderType.IMAP4 or MailProviderType.POP3 or MailProviderType.Exchange;

    /// <summary>
    /// Accounts whose mailbox server owns the folder tree and message state the way Outlook.com and
    /// Gmail do (as opposed to protocol accounts). Exchange counts: the server is the source of truth.
    /// </summary>
    public static bool IsProviderMailAccount(this MailProviderType providerType)
        => providerType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.Exchange;

    public static bool SupportsRemoteFolderSynchronization(this MailProviderType providerType)
        => providerType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4 or MailProviderType.Exchange;

    public static bool SupportsPushSynchronization(this MailProviderType providerType)
        => providerType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4 or MailProviderType.Exchange;

    public static bool UsesLocalMailState(this MailProviderType providerType)
        => providerType == MailProviderType.POP3;
}
