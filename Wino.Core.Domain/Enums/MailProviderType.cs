namespace Wino.Core.Domain.Enums;

public enum MailProviderType
{
    Outlook,
    Gmail,
    IMAP4 = 4 // 2-3 were removed after release. Don't change for backward compatibility.
}
