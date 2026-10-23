#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Services;

/// <summary>
/// Serves <see cref="CalendarCapabilities"/>. Outlook, Gmail and local calendars have fixed sets.
/// CalDAV values come from the state the synchronization engine recorded from the server: the DAV
/// header, collection properties and limits. Before the first discovery CalDAV calendars get
/// <see cref="CalendarCapabilities.CalDavDefault"/>.
/// </summary>
public sealed class CalendarCapabilityService : ICalendarCapabilityService
{
    internal const string ManagedAttachmentsCapability = "calendar-managed-attachments";
    internal const string AutoScheduleCapability = "calendar-auto-schedule";
    internal const string DefaultAlarmsCapability = "calendar-default-alarms";

    private static readonly ILogger Logger = Log.ForContext<CalendarCapabilityService>();
    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly ICalDavStateStore _stateStore;

    public CalendarCapabilityService(IAccountService accountService, ICalendarService calendarService, ICalDavStateStore stateStore)
    {
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
    }

    public async Task<CalendarCapabilities> GetAccountCapabilitiesAsync(Guid accountId)
    {
        try
        {
            var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
            if (account == null)
                return CalendarCapabilities.None;

            var source = ResolveSource(account);
            if (source != CalendarCapabilitySource.CalDav)
                return GetStaticCapabilities(source);

            var state = await _stateStore.GetAsync(accountId).ConfigureAwait(false);
            return FromCalDavState(state, calendar: null);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not resolve calendar capabilities of account {AccountId}", accountId);
            return CalendarCapabilities.None;
        }
    }

    public async Task<CalendarCapabilities> GetCalendarCapabilitiesAsync(Guid calendarId)
    {
        try
        {
            var calendar = await _calendarService.GetAccountCalendarAsync(calendarId).ConfigureAwait(false);
            if (calendar == null)
                return CalendarCapabilities.None;

            var account = await _accountService.GetAccountAsync(calendar.AccountId).ConfigureAwait(false);
            if (account == null)
                return CalendarCapabilities.None;

            var source = ResolveSource(account);
            CalendarCapabilities capabilities;

            if (source == CalendarCapabilitySource.CalDav)
            {
                var state = await _stateStore.GetAsync(account.Id).ConfigureAwait(false);
                capabilities = FromCalDavState(state, state.FindCalendar(calendar.RemoteCalendarId));
            }
            else
            {
                capabilities = GetStaticCapabilities(source);
            }

            return calendar.IsReadOnly && !capabilities.IsReadOnly
                ? capabilities with { IsReadOnly = true }
                : capabilities;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not resolve capabilities of calendar {CalendarId}", calendarId);
            return CalendarCapabilities.None;
        }
    }

    internal static CalendarCapabilitySource ResolveSource(MailAccount account)
        => CalendarCapabilities.ResolveSource(account);

    internal static CalendarCapabilities GetStaticCapabilities(CalendarCapabilitySource source)
        => CalendarCapabilities.ForSource(source);

    /// <summary>
    /// Maps recorded server state to capabilities. With <paramref name="calendar"/> null the result
    /// describes the account: limits are the strictest of its event calendars.
    /// </summary>
    internal static CalendarCapabilities FromCalDavState(CalDavAccountState state, CalDavCalendarState? calendar)
    {
        if (state == null || !state.HasDiscovery)
            return CalendarCapabilities.CalDavDefault;

        var calendars = calendar != null
            ? new List<CalDavCalendarState> { calendar }
            : state.Calendars.Values.Where(value => value.SupportsEvents).ToList();

        var hasDefaultAlarmValues = !string.IsNullOrWhiteSpace(state.DefaultAlarmVEventDateTime) ||
                                    !string.IsNullOrWhiteSpace(state.DefaultAlarmVEventDate) ||
                                    calendars.Any(value => !string.IsNullOrWhiteSpace(value.DefaultAlarmVEventDateTime) ||
                                                           !string.IsNullOrWhiteSpace(value.DefaultAlarmVEventDate));

        return new CalendarCapabilities
        {
            Source = CalendarCapabilitySource.CalDav,
            IsServerReported = true,
            IsReadOnly = calendars.Count > 0 && calendars.All(value => value.IsReadOnly),
            SupportsAttachments = state.HasDavCapability(ManagedAttachmentsCapability),
            SupportsAttendees = true,

            // RFC 6638 section 2: a server that schedules on the client's behalf says so in the
            // DAV header. Yahoo and Zimbra send no DAV header, so they are not assumed to.
            SendsInvitations = state.HasDavCapability(AutoScheduleCapability),
            SupportsOnlineMeeting = false,
            SupportsReminders = true,
            SupportsDefaultAlarms = state.HasDavCapability(DefaultAlarmsCapability) || hasDefaultAlarmValues,
            MaxAttendees = calendars.Select(value => value.MaxAttendeesPerInstance).Where(value => value > 0).Min(),
            QuotaAvailableBytes = calendars.Select(value => value.QuotaAvailableBytes).Where(value => value >= 0).Min(),
            QuotaUsedBytes = calendars.Select(value => value.QuotaUsedBytes).Where(value => value >= 0).Max(),

            // Yahoo reports 0, meaning no limit.
            MaxResourceSize = calendars.Select(value => value.MaxResourceSize).Where(value => value > 0).Min()
        };
    }
}
