using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Extensions;

public static class MailProviderTypeExtensions
{
    public static string GetProviderAssetName(this MailProviderType providerType)
        => providerType == MailProviderType.EAS ? nameof(MailProviderType.Outlook) : providerType.ToString();

    public static bool IsCustomMailProvider(this MailProviderType providerType)
        => providerType is MailProviderType.IMAP4 or MailProviderType.POP3 or MailProviderType.EAS;

    public static bool SupportsRemoteFolderSynchronization(this MailProviderType providerType)
        => providerType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4 or MailProviderType.EAS;

    public static bool SupportsPushSynchronization(this MailProviderType providerType)
        => providerType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4;

    public static bool UsesLocalMailState(this MailProviderType providerType)
        => providerType == MailProviderType.POP3;
}
