#nullable enable
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.CardDav;

namespace MailCal.Core.Synchronizers.CalDav;

/// <summary>
/// Server limits checked before a PUT, so a write the server would refuse fails with a clear
/// message instead of a generic error: <c>max-resource-size</c> and
/// <c>max-attendees-per-instance</c> (RFC 4791 section 5.2.5 and 5.2.9) and
/// <c>quota-available-bytes</c> (RFC 4331). The limits come from the last calendar listing in
/// <see cref="ICalDavStateStore"/>; the available quota is lowered after each successful write and
/// refreshed by the next listing.
/// </summary>
public sealed partial class CalDavSynchronizationEngine
{
    internal const string CalDavErrorNamespace = "urn:ietf:params:xml:ns:caldav";

    /// <summary>
    /// Pre-PUT hook: adds the pending default alarm of a new event, then refuses the write when it
    /// would break a known server limit. Returns the ICS to send.
    /// </summary>
    private async Task<string> PrepareOutgoingIcsAsync(
        MailAccount account,
        CalendarItem item,
        CalDavCalendar calendar,
        CalDavResourceSnapshot? snapshot,
        string icsContent,
        CancellationToken cancellationToken)
    {
        icsContent = ApplyPendingDefaultAlarm(item, snapshot == null, icsContent);

        var state = await _stateStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var calendarState = state.FindCalendar(calendar.RemoteCalendarId);

        if (calendarState != null)
            EnsureWithinServerLimits(calendarState, icsContent, snapshot?.IcsContent);

        return icsContent;
    }

    /// <summary>
    /// Throws a <see cref="DavRequestException"/> shaped like the server's own refusal (same status
    /// and precondition element) when <paramref name="icsContent"/> would exceed a limit.
    /// For an update, only the growth counts against the quota, and an event that already had more
    /// attendees than the limit may still be changed as long as none are added.
    /// </summary>
    internal static void EnsureWithinServerLimits(CalDavCalendarState calendarState, string icsContent, string? previousIcsContent)
    {
        ArgumentNullException.ThrowIfNull(calendarState);

        var newSize = GetResourceSize(icsContent);
        var previousSize = GetResourceSize(previousIcsContent);

        if (calendarState.MaxResourceSize is > 0 and var maxResourceSize && newSize > maxResourceSize)
        {
            throw new DavRequestException(403, Translator.DavError_ResourceTooLarge, [$"{{{CalDavErrorNamespace}}}max-resource-size"]);
        }

        if (calendarState.MaxAttendeesPerInstance is > 0 and var maxAttendees)
        {
            var attendees = CountMaximumAttendeesPerInstance(icsContent);

            if (attendees > maxAttendees && attendees > CountMaximumAttendeesPerInstance(previousIcsContent))
                throw new DavRequestException(403, Translator.DavError_TooManyAttendees, [$"{{{CalDavErrorNamespace}}}max-attendees-per-instance"]);
        }

        if (calendarState.QuotaAvailableBytes is long availableBytes && newSize - previousSize > availableBytes)
        {
            throw new DavRequestException(507, Translator.DavError_QuotaExceeded, ["{DAV:}quota-not-exceeded"]);
        }
    }

    /// <summary>
    /// Post-PUT hook: lowers (or raises) the known available quota by the size change. Approximate:
    /// the stored ICS stands in for the server copy, and the next listing replaces the value.
    /// </summary>
    private async Task RecordQuotaUsageAsync(
        MailAccount account,
        CalDavCalendar calendar,
        CalDavResourceSnapshot? snapshot,
        string icsContent,
        CancellationToken cancellationToken)
    {
        var delta = GetResourceSize(icsContent) - GetResourceSize(snapshot?.IcsContent);
        if (delta == 0)
            return;

        try
        {
            await _stateStore.UpdateAsync(account.Id, state =>
            {
                var calendarState = state.FindCalendar(calendar.RemoteCalendarId);
                if (calendarState != null)
                    ApplyQuotaDelta(calendarState, delta);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The write itself succeeded; a stale quota figure only weakens the next pre-check.
            Logger.Warning(ex, "Could not record CalDAV quota usage for calendar {CalendarId}", calendar.RemoteCalendarId);
        }
    }

    internal static void ApplyQuotaDelta(CalDavCalendarState calendarState, long delta)
    {
        if (calendarState.QuotaAvailableBytes is long available)
            calendarState.QuotaAvailableBytes = Math.Max(0, available - delta);

        if (calendarState.QuotaUsedBytes is long used)
            calendarState.QuotaUsedBytes = Math.Max(0, used + delta);
    }

    internal static long GetResourceSize(string? icsContent)
        => string.IsNullOrEmpty(icsContent) ? 0 : Encoding.UTF8.GetByteCount(icsContent);

    /// <summary>
    /// The largest number of ATTENDEE properties on one VEVENT (master or override), which is what
    /// <c>max-attendees-per-instance</c> limits. Attendees of e-mail alarms are not counted.
    /// </summary>
    internal static int CountMaximumAttendeesPerInstance(string? icsContent)
    {
        if (string.IsNullOrEmpty(icsContent))
            return 0;

        var maximum = 0;
        var current = 0;
        var insideEvent = false;
        var insideAlarm = false;

        foreach (var rawLine in icsContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // Folded continuation lines start with whitespace and never begin a property.
            if (line.Length == 0 || line[0] is ' ' or '\t')
                continue;

            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                insideEvent = true;
                current = 0;
            }
            else if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                insideEvent = false;
                maximum = Math.Max(maximum, current);
            }
            else if (line.Equals("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase))
            {
                insideAlarm = true;
            }
            else if (line.Equals("END:VALARM", StringComparison.OrdinalIgnoreCase))
            {
                insideAlarm = false;
            }
            else if (insideEvent && !insideAlarm &&
                     line.StartsWith("ATTENDEE", StringComparison.OrdinalIgnoreCase) &&
                     line.Length > 8 && line[8] is ':' or ';')
            {
                current++;
            }
        }

        return maximum;
    }
}
