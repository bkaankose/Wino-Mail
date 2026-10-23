#nullable enable
using System;
using System.Threading.Tasks;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Answers what an account's calendars, or one calendar, can do. Never returns null; an unknown
/// account or calendar yields <see cref="CalendarCapabilities.None"/>.
/// </summary>
public interface ICalendarCapabilityService
{
    Task<CalendarCapabilities> GetAccountCapabilitiesAsync(Guid accountId);

    Task<CalendarCapabilities> GetCalendarCapabilitiesAsync(Guid calendarId);
}
