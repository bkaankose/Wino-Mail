#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Models.Calendar;
using Wino.Services;

namespace Wino.Core.Synchronizers.CalDav;

/// <summary>
/// Answers to iMIP invitations that Wino sends itself (RFC 6047). The calendar write and the local
/// update are separate steps so the caller can send the reply in between and store nothing locally
/// when sending fails.
/// </summary>
public sealed partial class CalDavSynchronizationEngine
{
    /// <summary>
    /// Writes the attendee's copy of an invitation. An event already in the calendar is replaced with
    /// If-Match on its stored copy; a new one is created with If-None-Match. Nothing is stored locally.
    /// </summary>
    /// <param name="createContent">Builds the object to write from the stored copy, or from nothing for a new event.</param>
    /// <returns>The server copy before the write (null for a new event) and the written copy.</returns>
    internal async Task<(CalDavResourceSnapshot? Previous, CalDavResourceSnapshot Written)> WriteInvitationCopyAsync(
        MailAccount account,
        Guid calendarId,
        CalendarItem? existingItem,
        string uid,
        Func<string?, string> createContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(createContent);

        var (connection, calendar) = await ResolveWriteContextAsync(account, calendarId, cancellationToken).ConfigureAwait(false);

        using (await CalDavConnection.LockCalendarAsync(calendarId, cancellationToken).ConfigureAwait(false))
        {
            CalDavResourceSnapshot? snapshot = null;
            if (existingItem != null)
                snapshot = await RequireResourceSnapshotAsync(account, existingItem).ConfigureAwait(false);

            var icsContent = createContent(snapshot?.IcsContent);
            var state = await _stateStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
            if (state.FindCalendar(calendar.RemoteCalendarId) is { } calendarState)
                EnsureWithinServerLimits(calendarState, icsContent, snapshot?.IcsContent);

            var result = await _client
                .UpsertCalendarEventAsync(connection, calendar, new CalDavWriteRequest
                {
                    RemoteEventId = existingItem?.RemoteEventId.GetProviderRemoteEventId() ?? uid,
                    ExactHref = snapshot?.ExactHref ?? string.Empty,
                    ETag = snapshot?.ETag ?? string.Empty,
                    IcsContent = icsContent,
                    CreateOnly = snapshot == null
                }, cancellationToken)
                .ConfigureAwait(false);

            await RecordQuotaUsageAsync(account, calendar, snapshot, icsContent, cancellationToken).ConfigureAwait(false);
            var written = await ReadBackWriteAsync(connection, result, icsContent, cancellationToken).ConfigureAwait(false);
            return (snapshot, written);
        }
    }

    /// <summary>
    /// Undoes <see cref="WriteInvitationCopyAsync"/> when the reply could not be sent: a created
    /// resource is deleted and a replaced one gets its previous copy back, both conditional on the
    /// written ETag so a change made since is never overwritten. Throws when the server refuses.
    /// </summary>
    internal async Task RevertInvitationCopyAsync(
        MailAccount account,
        Guid calendarId,
        CalendarItem? existingItem,
        CalDavResourceSnapshot? previous,
        CalDavResourceSnapshot written,
        string uid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(written);

        var (connection, calendar) = await ResolveWriteContextAsync(account, calendarId, cancellationToken).ConfigureAwait(false);

        using (await CalDavConnection.LockCalendarAsync(calendarId, cancellationToken).ConfigureAwait(false))
        {
            if (previous == null)
            {
                await _client.DeleteCalendarEventAsync(connection, written.ExactHref, written.ETag, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await _client
                .UpsertCalendarEventAsync(connection, calendar, new CalDavWriteRequest
                {
                    RemoteEventId = existingItem?.RemoteEventId.GetProviderRemoteEventId() ?? uid,
                    ExactHref = written.ExactHref,
                    ETag = written.ETag,
                    IcsContent = previous.IcsContent
                }, cancellationToken)
                .ConfigureAwait(false);

            // The stored copy is the previous one already; only its ETag changed.
            if (existingItem != null)
            {
                var restored = await ReadBackWriteAsync(connection, result, previous.IcsContent, cancellationToken).ConfigureAwait(false);
                await SaveSeriesSnapshotAsync(account, existingItem, restored.ExactHref, restored.ETag, restored.IcsContent).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Stores a written invitation copy the way a synchronization stores a downloaded resource, so
    /// the calendar shows it (and hides it when declined) without waiting for the next sync.
    /// </summary>
    internal async Task ApplyInvitationCopyAsync(
        MailAccount account,
        Guid calendarId,
        CalendarItem? existingItem,
        CalDavResourceSnapshot written,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(written);

        if (existingItem != null)
            await SaveSeriesSnapshotAsync(account, existingItem, written.ExactHref, written.ETag, written.IcsContent).ConfigureAwait(false);

        var context = await CreateWriteContextAsync(account, existingItem ?? new CalendarItem { CalendarId = calendarId }, cancellationToken).ConfigureAwait(false);
        if (context == null)
            return;

        using (await CalDavConnection.LockCalendarAsync(calendarId, cancellationToken).ConfigureAwait(false))
        {
            var shadows = await LoadShadowsAsync(context).ConfigureAwait(false);
            var events = _client.ExpandCalendarResource(written.IcsContent, written.ExactHref, written.ETag, context.PeriodStartUtc, context.PeriodEndUtc)
                         ?? Array.Empty<CalDavCalendarEvent>();

            await ApplyExpandedEventsAsync(
                context,
                shadows,
                written.ExactHref,
                written.ETag,
                written.IcsContent,
                events,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                reapplyUnchanged: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The user's answer on the stored rows, for identities the server's address set does not list
    /// (an alias, or a forwarded invitation answered with the account address).
    /// </summary>
    internal Task PersistInvitationResponseAsync(MailAccount account, CalendarItem item, CalDavUserIdentity identity, AttendeeStatus response)
        => PersistOwnResponseAsync(account, item, identity, MapParticipationToResponse(response), response);

    internal static CalendarItemStatus MapParticipationToResponse(AttendeeStatus participation)
        => participation switch
        {
            AttendeeStatus.Accepted => CalendarItemStatus.Accepted,
            AttendeeStatus.Tentative => CalendarItemStatus.Tentative,
            AttendeeStatus.Declined => CalendarItemStatus.Cancelled,
            _ => CalendarItemStatus.NotResponded
        };
}
