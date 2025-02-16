using Wino.Core.Domain.Models.Calendar;

namespace Wino.Messaging.Client.Calendar;

/// <summary>
/// Raised when calendar's visible date range is updated.
/// Used to update the background of the visible date range in CalendarView.
/// </summary>
/// <param name="DateRange">New visible date range.</param>
public record VisibleDateRangeChangedMessage(DateRange DateRange);
