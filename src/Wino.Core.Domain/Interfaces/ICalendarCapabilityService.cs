#nullable enable
using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Answers what an account's calendars, or one calendar, can do. Never returns null; an unknown
/// account or calendar yields <see cref="CalendarCapabilities.None"/>.
/// </summary>
public interface ICalendarCapabilityService
{
    Task<CalendarCapabilities> GetAccountCapabilitiesAsync(Guid accountId);

    Task<CalendarCapabilities> GetCalendarCapabilitiesAsync(Guid calendarId);
}
