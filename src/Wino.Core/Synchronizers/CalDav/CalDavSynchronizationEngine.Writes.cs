#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services;

namespace Wino.Core.Synchronizers.CalDav;

/// <summary>
/// Writes of an existing event and their entity tags. The stored copy is the base of every edit,
/// so it must be the server's copy with the server's strong ETag: a server that changed the data it
/// stored answers the PUT without one (RFC 4791 section 5.3.4) and the copy is read back. When the
/// server copy changed meanwhile (412, RFC 7232 section 3.1), only the fields the user changed are
/// applied to the current copy, unless another client changed one of those fields too: then nothing
/// is overwritten, the local event shows the server's version and the edit fails as a conflict.
/// </summary>
public sealed partial class CalDavSynchronizationEngine
{
    /// <returns>The stored ICS the edit started from.</returns>
    private async Task<string> WriteEventEditAsync(
        MailAccount account,
        CalendarItem item,
        List<CalendarEventAttendee> attendees,
        CalDavUserIdentity identity,
        CalDavConnectionSettings connection,
        CalDavCalendar calendar,
        CalDavResourceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var edit = CalDavIcsMutator.EditEvent(snapshot.IcsContent, item, attendees, identity);
        if (!edit.HasChanges)
            return snapshot.IcsContent;

        var writeBase = snapshot;
        string? icsContent = edit.IcsContent;

        if (string.IsNullOrWhiteSpace(snapshot.ETag))
        {
            // Copies stored without a validator (a PUT answered without ETag before read-back existed)
            // are compared with the server copy first instead of being written blindly.
            (writeBase, icsContent) = await RebaseEditAsync(account, item, attendees, identity, connection, snapshot, edit, cancellationToken).ConfigureAwait(false);
            if (icsContent == null)
                return snapshot.IcsContent;
        }

        icsContent = await PrepareOutgoingIcsAsync(account, item, calendar, writeBase, icsContent, cancellationToken).ConfigureAwait(false);

        CalDavWriteResult result;
        try
        {
            result = await PutEventAsync(connection, calendar, item, writeBase, icsContent, cancellationToken).ConfigureAwait(false);
        }
        catch (DavRequestException ex) when (ex.StatusCode == 412 && ReferenceEquals(writeBase, snapshot))
        {
            Logger.Information("CalDAV event {Href} changed on the server since it was stored; applying the edit to the current copy", snapshot.ExactHref);
            (writeBase, icsContent) = await RebaseEditAsync(account, item, attendees, identity, connection, snapshot, edit, cancellationToken).ConfigureAwait(false);
            if (icsContent == null)
                return snapshot.IcsContent;

            icsContent = await PrepareOutgoingIcsAsync(account, item, calendar, writeBase, icsContent, cancellationToken).ConfigureAwait(false);
            result = await PutEventAsync(connection, calendar, item, writeBase, icsContent, cancellationToken).ConfigureAwait(false);
        }

        await RecordQuotaUsageAsync(account, calendar, writeBase, icsContent, cancellationToken).ConfigureAwait(false);
        var written = await ReadBackWriteAsync(connection, result, icsContent, cancellationToken).ConfigureAwait(false);
        await SaveSeriesSnapshotAsync(account, item, written.ExactHref, written.ETag, written.IcsContent).ConfigureAwait(false);

        return snapshot.IcsContent;
    }

    /// <summary>
    /// Reads the current server copy and applies the fields of <paramref name="edit"/> to it. Returns
    /// a null ICS when the server copy already has them (it is then stored). Throws a 412
    /// <see cref="DavRequestException"/> after refreshing the local event when the resource is gone,
    /// has no ETag, or another client changed one of the same fields.
    /// </summary>
    private async Task<(CalDavResourceSnapshot Base, string? IcsContent)> RebaseEditAsync(
        MailAccount account,
        CalendarItem item,
        List<CalendarEventAttendee> attendees,
        CalDavUserIdentity identity,
        CalDavConnectionSettings connection,
        CalDavResourceSnapshot snapshot,
        CalDavEventEdit edit,
        CancellationToken cancellationToken)
    {
        var current = await _client.GetEventResourceAsync(connection, snapshot.ExactHref, cancellationToken).ConfigureAwait(false);
        if (current == null || current.IsDeleted || string.IsNullOrWhiteSpace(current.IcsContent))
        {
            Logger.Information("CalDAV event {Href} was removed on the server; the edit is not written", snapshot.ExactHref);
            throw new DavRequestException(412, Translator.DavError_Conflict);
        }

        var currentSnapshot = new CalDavResourceSnapshot
        {
            ExactHref = snapshot.ExactHref,
            ETag = current.ETag ?? string.Empty,
            IcsContent = current.IcsContent
        };

        var conflicting = edit.ChangedFields
            .Where(field => !CalDavIcsMutator.ReadFieldLines(snapshot.IcsContent, item.RemoteEventId, field)
                .SequenceEqual(CalDavIcsMutator.ReadFieldLines(current.IcsContent, item.RemoteEventId, field), StringComparer.Ordinal))
            .ToList();

        if (conflicting.Count > 0 || string.IsNullOrWhiteSpace(currentSnapshot.ETag))
        {
            Logger.Information("CalDAV event {Href}: another client changed {Fields}; keeping the server version", snapshot.ExactHref, string.Join(", ", conflicting));
            await RefreshFromServerAsync(account, item, currentSnapshot, cancellationToken).ConfigureAwait(false);
            throw new DavRequestException(412, Translator.DavError_Conflict);
        }

        var rebased = CalDavIcsMutator.EditEvent(current.IcsContent, item, attendees, identity, edit.ChangedFields);
        if (rebased.HasChanges)
            return (currentSnapshot, rebased.IcsContent);

        await SaveSeriesSnapshotAsync(account, item, currentSnapshot.ExactHref, currentSnapshot.ETag, currentSnapshot.IcsContent).ConfigureAwait(false);
        return (currentSnapshot, null);
    }

    private Task<CalDavWriteResult> PutEventAsync(
        CalDavConnectionSettings connection,
        CalDavCalendar calendar,
        CalendarItem item,
        CalDavResourceSnapshot writeBase,
        string icsContent,
        CancellationToken cancellationToken)
        => _client.UpsertCalendarEventAsync(connection, calendar, new CalDavWriteRequest
        {
            RemoteEventId = item.RemoteEventId.GetProviderRemoteEventId(),
            ExactHref = writeBase.ExactHref,
            ETag = writeBase.ETag,
            IcsContent = icsContent
        }, cancellationToken);

    /// <summary>
    /// The href, ETag and text to store after a successful PUT. Without a strong ETag in the answer
    /// the server stored something other than what was sent (RFC 4791 section 5.3.4), so its copy is
    /// read back; a failed read keeps what was sent and leaves the ETag empty for the next edit to resolve.
    /// </summary>
    private async Task<CalDavResourceSnapshot> ReadBackWriteAsync(
        CalDavConnectionSettings connection,
        CalDavWriteResult result,
        string sentIcsContent,
        CancellationToken cancellationToken)
    {
        var written = new CalDavResourceSnapshot { ExactHref = result.ExactHref, ETag = result.ETag ?? string.Empty, IcsContent = sentIcsContent };
        if (!result.RequiresRefetch)
            return written;

        try
        {
            var fetched = await _client.GetEventResourceAsync(connection, result.ExactHref, cancellationToken).ConfigureAwait(false);
            if (fetched != null && !fetched.IsDeleted && !string.IsNullOrWhiteSpace(fetched.IcsContent))
                return new CalDavResourceSnapshot { ExactHref = result.ExactHref, ETag = fetched.ETag ?? string.Empty, IcsContent = fetched.IcsContent };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The write itself succeeded; failing here would revert a change the server has.
            Logger.Warning(ex, "Could not read back CalDAV resource {Href} after writing it", result.ExactHref);
        }

        return written;
    }

    /// <summary>Stores the server copy of a resource and updates the local events from it. Runs under the calendar lock.</summary>
    private async Task RefreshFromServerAsync(MailAccount account, CalendarItem item, CalDavResourceSnapshot current, CancellationToken cancellationToken)
    {
        try
        {
            var context = await CreateWriteContextAsync(account, item, cancellationToken).ConfigureAwait(false);
            if (context == null)
                return;

            var shadows = await LoadShadowsAsync(context).ConfigureAwait(false);
            var events = _client.ExpandCalendarResource(current.IcsContent, current.ExactHref, current.ETag, context.PeriodStartUtc, context.PeriodEndUtc)
                         ?? Array.Empty<CalDavCalendarEvent>();

            await ApplyExpandedEventsAsync(
                context,
                shadows,
                current.ExactHref,
                current.ETag,
                current.IcsContent,
                events,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                reapplyUnchanged: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The next synchronization downloads the resource; the conflict is reported either way.
            Logger.Warning(ex, "Could not refresh CalDAV event {Href} from the server", current.ExactHref);
        }
    }

    /// <summary>A calendar context for applying one resource outside a synchronization, or null when the calendar is unknown.</summary>
    private async Task<CalendarContext?> CreateWriteContextAsync(MailAccount account, CalendarItem item, CancellationToken cancellationToken)
    {
        var localCalendar = await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);
        if (localCalendar == null || string.IsNullOrWhiteSpace(localCalendar.RemoteCalendarId))
            return null;

        var settings = await CalDavConnection.CreateRequiredSettingsAsync(account, _autoDiscoveryService, cancellationToken).ConfigureAwait(false);
        var state = await _stateStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var remoteCalendar = new CalDavCalendar { RemoteCalendarId = localCalendar.RemoteCalendarId, Name = localCalendar.Name };
        var calendarState = state.FindCalendar(localCalendar.RemoteCalendarId)
                            ?? new CalDavCalendarState { RemoteCalendarId = localCalendar.RemoteCalendarId };

        var now = DateTimeOffset.UtcNow;
        var (_, storedWindowEndUtc) = CalendarSyncWindowToken.Decode(localCalendar.SynchronizationDeltaToken);
        return new CalendarContext(
            account,
            settings,
            localCalendar,
            remoteCalendar,
            calendarState,
            now.AddYears(-1),
            storedWindowEndUtc ?? now.AddYears(2),
            ForceSync: false);
    }
}
