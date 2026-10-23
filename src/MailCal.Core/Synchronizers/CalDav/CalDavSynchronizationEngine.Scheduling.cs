#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Extensions;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Services;

namespace MailCal.Core.Synchronizers.CalDav;

/// <summary>
/// Implicit scheduling (RFC 6638): the server sends invitations and replies from what the client
/// stores, so the client must write the user's identity and answers the way the server expects.
/// The user is identified by the server's calendar-user-address-set. As organizer the user edits
/// the event (STATUS and SEQUENCE change); as attendee the user only changes their own PARTSTAT.
/// </summary>
public sealed partial class CalDavSynchronizationEngine
{
    /// <summary>
    /// The user's scheduling identity from the discovered calendar-user-address-set, falling back
    /// to the account address before the first discovery.
    /// </summary>
    internal async Task<CalDavUserIdentity> GetUserIdentityAsync(MailAccount account, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> addresses = Array.Empty<string>();

        try
        {
            var state = await _stateStore.GetAsync(account.Id, cancellationToken).ConfigureAwait(false);
            addresses = state.CalendarUserAddresses;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Could not read the CalDAV address set of account {AccountId}; using the account address", account.Id);
        }

        return CalDavUserIdentity.Resolve(addresses, account.Address);
    }

    /// <summary>
    /// Sets ORGANIZER of an event the user organizes to the address the server knows the user by.
    /// An event organized by someone else keeps its organizer, and an event without one stays
    /// without one (the mutator adds it only when attendees make it a scheduled event).
    /// </summary>
    private async Task<CalDavUserIdentity> ApplyUserIdentityAsync(MailAccount account, CalendarItem item, CancellationToken cancellationToken)
    {
        var identity = await GetUserIdentityAsync(account, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(item.OrganizerEmail))
            item.OrganizerEmail = identity.ResolveOrganizerAddress(item.OrganizerEmail);

        return identity;
    }

    /// <summary>
    /// Answers an invitation as an attendee: the stored copy gets the user's PARTSTAT and nothing
    /// else changes (no STATUS, no SEQUENCE), then it is written back with If-Match. Returns false
    /// when the user organizes the event or no stored copy exists; the caller then keeps the
    /// organizer behavior.
    /// </summary>
    private async Task<bool> TryRespondAsAttendeeAsync(
        MailAccount account,
        CalendarItem item,
        CalendarItemStatus response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);

        if (string.IsNullOrWhiteSpace(item.RemoteEventId))
            return false;

        var identity = await GetUserIdentityAsync(account, cancellationToken).ConfigureAwait(false);
        var stored = await _changeProcessor
            .GetCalendarItemIcsAsync(account.Id, item.CalendarId, item.Id)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stored?.IcsContent) ||
            !CalDavIcsMutator.IsOrganizedByOther(stored.IcsContent, item.RemoteEventId, identity))
        {
            return false;
        }

        var (connection, calendar) = await ResolveWriteContextAsync(account, item.CalendarId, cancellationToken).ConfigureAwait(false);

        using (await CalDavConnection.LockCalendarAsync(item.CalendarId, cancellationToken).ConfigureAwait(false))
        {
            // Read again under the lock: a sync may have stored a newer copy meanwhile.
            var snapshot = await RequireResourceSnapshotAsync(account, item).ConfigureAwait(false);
            var participation = MapResponseToParticipation(response);
            var icsContent = CalDavIcsMutator.SetOwnParticipation(snapshot.IcsContent, item.RemoteEventId, identity, participation);
            icsContent = await PrepareOutgoingIcsAsync(account, item, calendar, snapshot, icsContent, cancellationToken).ConfigureAwait(false);

            var result = await _client
                .UpsertCalendarEventAsync(connection, calendar, new CalDavWriteRequest
                {
                    RemoteEventId = item.RemoteEventId.GetProviderRemoteEventId(),
                    ExactHref = snapshot.ExactHref,
                    ETag = snapshot.ETag,
                    IcsContent = icsContent
                }, cancellationToken)
                .ConfigureAwait(false);

            await RecordQuotaUsageAsync(account, calendar, snapshot, icsContent, cancellationToken).ConfigureAwait(false);
            await SaveSeriesSnapshotAsync(account, item, result.ExactHref, result.ETag, icsContent).ConfigureAwait(false);
            await PersistOwnResponseAsync(account, item, identity, response, participation).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Stores the answer locally the way the pages expect it: the item status (a decline hides the
    /// event, as before) and the user's own attendee row. A series answer applies to its occurrences.
    /// </summary>
    private async Task PersistOwnResponseAsync(
        MailAccount account,
        CalendarItem item,
        CalDavUserIdentity identity,
        CalendarItemStatus response,
        AttendeeStatus participation)
    {
        var items = new List<CalendarItem> { item };

        if (item.IsRecurringParent)
        {
            var children = await _calendarService.GetRecurringChildrenAsync(item.Id).ConfigureAwait(false);
            items.AddRange((children ?? []).Where(child => child.Id != item.Id));
        }

        foreach (var target in items)
        {
            target.Status = response;
            if (response == CalendarItemStatus.Cancelled)
                target.IsHidden = true;

            var attendees = await _calendarService.GetAttendeesAsync(target.Id).ConfigureAwait(false) ?? [];
            var own = attendees.Where(attendee => identity.IsUser(attendee.Email)).ToList();

            if (own.Count == 0 && !string.IsNullOrWhiteSpace(identity.OrganizerAddress))
            {
                var added = new CalendarEventAttendee
                {
                    Id = Guid.NewGuid(),
                    CalendarItemId = target.Id,
                    Email = identity.OrganizerAddress,
                    Name = string.IsNullOrWhiteSpace(account.SenderName) ? identity.OrganizerAddress : account.SenderName
                };

                attendees.Add(added);
                own.Add(added);
            }

            foreach (var attendee in own)
                attendee.AttendenceStatus = participation;

            await _calendarService.UpdateCalendarItemAsync(target, attendees).ConfigureAwait(false);
        }
    }

    internal static AttendeeStatus MapResponseToParticipation(CalendarItemStatus response)
        => response switch
        {
            CalendarItemStatus.Accepted => AttendeeStatus.Accepted,
            CalendarItemStatus.Tentative => AttendeeStatus.Tentative,
            CalendarItemStatus.Cancelled => AttendeeStatus.Declined,
            _ => AttendeeStatus.NeedsAction
        };

    /// <summary>
    /// The local status of an imported event the user was invited to, from the user's own
    /// PARTSTAT. STATUS only says whether the organizer cancelled the event; a cancelled event
    /// stays cancelled. Returns null when the user organizes the event or is not an attendee,
    /// and STATUS then decides as before.
    /// </summary>
    internal static CalendarItemStatus? ResolveImportedResponseStatus(CalDavCalendarEvent remoteEvent, CalDavUserIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(remoteEvent);
        ArgumentNullException.ThrowIfNull(identity);

        if (remoteEvent.Status == CalendarItemStatus.Cancelled ||
            string.IsNullOrWhiteSpace(remoteEvent.OrganizerEmail) ||
            identity.IsUser(remoteEvent.OrganizerEmail))
        {
            return null;
        }

        var own = remoteEvent.Attendees.FirstOrDefault(attendee => identity.IsUser(attendee.Email));
        if (own == null)
            return null;

        return own.AttendenceStatus switch
        {
            AttendeeStatus.Accepted => CalendarItemStatus.Accepted,
            AttendeeStatus.Tentative => CalendarItemStatus.Tentative,
            AttendeeStatus.Declined => CalendarItemStatus.Cancelled,
            _ => CalendarItemStatus.NotResponded
        };
    }

    /// <summary>
    /// Applies <see cref="ResolveImportedResponseStatus"/> to an event about to be stored: an
    /// invitation the user has not answered shows as not responded, and one the user declined is
    /// hidden, the same way a decline made in Mail-Cal hides it.
    /// </summary>
    internal static void ApplyImportedResponseStatus(CalDavCalendarEvent remoteEvent, CalDavUserIdentity identity)
    {
        var status = ResolveImportedResponseStatus(remoteEvent, identity);
        if (status == null)
            return;

        remoteEvent.Status = status.Value;
        remoteEvent.IsHidden = status == CalendarItemStatus.Cancelled;
    }
}
