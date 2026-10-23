using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.Client.Calendar;

/// <summary>
/// Raised when calendar type is changed like Day,Week,Month etc.
/// </summary>
/// <param name="NewDisplayType">New type.</param>
public record CalendarDisplayTypeChangedMessage(CalendarDisplayType NewDisplayType);
