using System;

namespace Wino.Core.Domain.Models.Calendar;

/// <summary>
/// Contains the clicked date on the calendar view.
/// </summary>
/// <param name="ClickedDate">Requested date.</param>
public record CalendarViewDayClickedEventArgs(DateTime ClickedDate);
