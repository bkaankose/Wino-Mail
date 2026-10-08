using Wino.Core.Domain.Enums;

namespace Wino.Messaging.Client.Calendar;

/// <summary>
/// Raised when calendar type is changed like Day,Week,Month etc.
/// </summary>
/// <param name="NewDisplayType">New type.</param>
public record CalendarDisplayTypeChangedMessage(CalendarDisplayType NewDisplayType);
