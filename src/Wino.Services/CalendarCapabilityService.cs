#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Services;

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
    private readonly IDatabaseService _databaseService;

    public CalendarCapabilityService(IAccountService accountService, ICalendarService calendarService, ICalDavStateStore stateStore, IDatabaseService databaseService)
    {
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
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
                return await GetProviderCapabilitiesAsync(account, source).ConfigureAwait(false);

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
                capabilities = await GetProviderCapabilitiesAsync(account, source).ConfigureAwait(false);
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
    /// The fixed set of the source, narrowed for Exchange by transport. MAPI over HTTP keeps a changed occurrence's files
    /// on its Exception Embedded Message (MS-OXOCAL 2.2.10.2), which Wino does not write. Below ActiveSync 16.0 the server
    /// sends no invitations, updates or meeting answers, so the account offers no attendees. ActiveSync calendar items
    /// carry attachments only in 16.0 and 16.1 (MS-ASCAL, MS-ASAIRS 2.2.2.2), and not while the device policy disallows
    /// attachments (MS-ASPROV AttachmentsEnabled); before the version is negotiated they are not offered.
    /// </summary>
    private async Task<CalendarCapabilities> GetProviderCapabilitiesAsync(MailAccount account, CalendarCapabilitySource source)
    {
        var capabilities = GetStaticCapabilities(source);
        if (source != CalendarCapabilitySource.Exchange)
            return capabilities;

        var transport = account.ServerInformation?.EffectiveExchangeTransport;
        if (transport == ExchangeTransport.MapiHttp)
            return capabilities with { SupportsOccurrenceAttachmentChanges = false };
        if (transport != ExchangeTransport.ActiveSync)
            return capabilities;

        var state = await _databaseService.Connection.FindAsync<EasAccountState>(account.Id).ConfigureAwait(false);
        var hasVersion = Version.TryParse(state?.ProtocolVersion, out var version);
        if (hasVersion && version < new Version(16, 0))
            capabilities = capabilities with { SupportsAttendees = false, SendsInvitations = false };

        var supportsAttachments = hasVersion && version >= new Version(16, 0) && state?.PolicyAttachmentsDisabled == false;
        return supportsAttachments
            ? capabilities
            : capabilities with { SupportsAttachments = false, SupportsAttachmentChanges = false, SupportsOccurrenceAttachmentChanges = false };
    }

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
            SupportsAttachmentChanges = state.HasDavCapability(ManagedAttachmentsCapability),
            SupportsOccurrenceAttachmentChanges = state.HasDavCapability(ManagedAttachmentsCapability),
            SupportsAttendees = true,

            // RFC 6638 section 2: a server that schedules on the client's behalf says so in the
            // DAV header. Yahoo and Zimbra send no DAV header, so they are not assumed to.
            SendsInvitations = state.HasDavCapability(AutoScheduleCapability),
            SupportsOnlineMeeting = false,
            SupportsReminders = true,
            SupportsReminderAfterStart = true,
            SupportsDefaultAlarms = state.HasDavCapability(DefaultAlarmsCapability) || hasDefaultAlarmValues,
            MaxAttendees = calendars.Select(value => value.MaxAttendeesPerInstance).Where(value => value > 0).Min(),
            QuotaAvailableBytes = calendars.Select(value => value.QuotaAvailableBytes).Where(value => value >= 0).Min(),
            QuotaUsedBytes = calendars.Select(value => value.QuotaUsedBytes).Where(value => value >= 0).Max(),

            // Yahoo reports 0, meaning no limit.
            MaxResourceSize = calendars.Select(value => value.MaxResourceSize).Where(value => value > 0).Min()
        };
    }
}
