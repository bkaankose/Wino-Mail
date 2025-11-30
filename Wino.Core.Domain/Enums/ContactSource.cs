namespace Wino.Core.Domain.Enums;

/// <summary>
/// Represents the source/origin of a contact.
/// </summary>
public enum ContactSource
{
    /// <summary>
    /// Contact was manually created by the user in Wino Mail.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Contact was synchronized from Gmail People API.
    /// </summary>
    Gmail = 1,

    /// <summary>
    /// Contact was synchronized from Outlook/Microsoft Graph Contacts API.
    /// </summary>
    Outlook = 2,

    /// <summary>
    /// Contact was extracted from IMAP email headers or imported from vCard.
    /// </summary>
    IMAP = 3,

    /// <summary>
    /// Contact was automatically extracted from sent/received email addresses.
    /// </summary>
    EmailExtraction = 4
}
