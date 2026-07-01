namespace Wino.Core.Domain.Enums;

public enum MailProviderType
{
    Outlook,
    Gmail,
    IMAP4 = 4, // 2-3 were removed after release. Don't change for backward compatibility.
    Exchange = 5 // On-premises Exchange via EWS (Exchange Web Services).
}
