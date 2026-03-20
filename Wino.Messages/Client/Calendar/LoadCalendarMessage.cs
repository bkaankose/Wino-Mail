using Wino.Core.Domain.Models.Calendar;

namespace Wino.Messaging.Client.Calendar;

/// <summary>
/// Raised when a new calendar display range is requested.
/// </summary>
/// <param name="DisplayRequest">Display type and anchor date to resolve.</param>
/// <param name="ForceReload">Force a reload even if the resolved range did not change.</param>
public record LoadCalendarMessage(CalendarDisplayRequest DisplayRequest, bool ForceReload = false);
