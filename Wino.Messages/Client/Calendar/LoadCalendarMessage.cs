using System;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.Client.Calendar;

/// <summary>
/// Raised when a new calendar range is requested for drawing.
/// </summary>
/// <param name="DisplayType">Type of the calendar.</param>
/// <param name="DisplayDate">Exact date to highlight.</param>
/// <param name="DayDisplayCount">How many days to load with Day calendar display type.</param>
/// <param name="ForceRedraw">Remove all days and force re-render of everything. Used when settings are updated.</param>
public record LoadCalendarMessage(DateTime DisplayDate, CalendarInitInitiative CalendarInitInitiative, bool ForceRedraw = false);
