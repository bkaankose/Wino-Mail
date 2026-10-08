namespace Wino.Core.Domain.Enums;

/// <summary>
/// Represents the type of mail item.
/// </summary>
public enum MailItemType
{
    /// <summary>
    /// Regular mail message.
    /// </summary>
    Mail = 0,

    /// <summary>
    /// Calendar invitation (meeting request).
    /// </summary>
    CalendarInvitation = 1,

    /// <summary>
    /// Calendar response (meeting accepted, tentatively accepted, or declined).
    /// </summary>
    CalendarResponse = 2,

    /// <summary>
    /// Calendar cancellation (meeting cancelled).
    /// </summary>
    CalendarCancellation = 3
}
