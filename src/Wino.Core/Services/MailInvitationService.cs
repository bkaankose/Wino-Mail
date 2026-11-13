#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Core.Integration.Processors;
using Wino.Core.Synchronizers.CalDav;
using Wino.Services;

namespace Wino.Core.Services;

/// <summary>
/// Invitations of IMAP and POP3 accounts are answered the iMIP way (RFC 6047): the attendee's copy
/// goes into the account's calendar (CalDAV or local) and Wino mails the REPLY to the organizer.
/// Outlook and Gmail answer through their calendar API when the invitation's event is mapped.
/// </summary>
public sealed class MailInvitationService : IMailInvitationService
{
    private static readonly ILogger Logger = Log.ForContext<MailInvitationService>();

    // One answer at a time: two answers to the same invitation must not interleave their writes.
    private static readonly SemaphoreSlim AnswerGate = new(1, 1);

    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly IMailService _mailService;
    private readonly IImapChangeProcessor _changeProcessor;
    private readonly CalDavSynchronizationEngine _calDavEngine;
    private readonly ICalDavClient _calDavClient;
    private readonly ISmtpTransport _smtpTransport;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly IPreferencesService _preferencesService;

    public MailInvitationService(
        IAccountService accountService,
        ICalendarService calendarService,
        IMailService mailService,
        IImapChangeProcessor changeProcessor,
        CalDavSynchronizationEngine calDavEngine,
        ICalDavClient calDavClient,
        ISmtpTransport smtpTransport,
        IWinoRequestDelegator requestDelegator,
        IPreferencesService preferencesService)
    {
        _accountService = accountService;
        _calendarService = calendarService;
        _mailService = mailService;
        _changeProcessor = changeProcessor;
        _calDavEngine = calDavEngine;
        _calDavClient = calDavClient;
        _smtpTransport = smtpTransport;
        _requestDelegator = requestDelegator;
        _preferencesService = preferencesService;
    }

    public MailInvitation? Parse(MimeMessage message) => MailInvitationReader.Parse(message);

    public async Task<MailInvitationState?> GetStateAsync(MailCopy mailCopy, MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailCopy);
        ArgumentNullException.ThrowIfNull(message);

        var invitation = Parse(message);
        await CorrectItemTypeAsync(mailCopy, message).ConfigureAwait(false);

        if (invitation == null || invitation.Method == MailInvitationMethod.Other)
            return null;

        var context = await LoadContextAsync(mailCopy, invitation, cancellationToken).ConfigureAwait(false);
        return await BuildStateAsync(context).ConfigureAwait(false);
    }

    public async Task<MailInvitationState> RespondAsync(
        MailCopy mailCopy,
        MimeMessage message,
        AttendeeStatus response,
        string? comment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailCopy);
        ArgumentNullException.ThrowIfNull(message);

        if (response is not (AttendeeStatus.Accepted or AttendeeStatus.Tentative or AttendeeStatus.Declined))
            throw new ArgumentOutOfRangeException(nameof(response));

        var invitation = Parse(message);
        if (invitation == null || invitation.Method != MailInvitationMethod.Request)
            throw new MailInvitationException(Translator.MailInvitationService_CannotRespond);

        await AnswerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await LoadContextAsync(mailCopy, invitation, cancellationToken).ConfigureAwait(false);
            var state = await BuildStateAsync(context).ConfigureAwait(false);

            if (!state.CanRespond)
                throw new MailInvitationException(string.IsNullOrEmpty(state.UnavailableReason) ? Translator.MailInvitationService_CannotRespond : state.UnavailableReason);

            switch (context.Mode)
            {
                case CalendarMode.Provider:
                    await RespondThroughProviderAsync(context, response, comment).ConfigureAwait(false);
                    break;
                case CalendarMode.CalDav:
                    await RespondWithCalDavAsync(context, mailCopy, response, comment, cancellationToken).ConfigureAwait(false);
                    break;
                case CalendarMode.Local:
                    await RespondWithLocalCalendarAsync(context, mailCopy, response, comment, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new MailInvitationException(Translator.MailInvitationService_CalendarUnavailable);
            }

            var refreshed = await LoadContextAsync(mailCopy, invitation, cancellationToken).ConfigureAwait(false);
            var result = await BuildStateAsync(refreshed).ConfigureAwait(false);

            // Provider answers are queued; the stored event shows the answer once the request runs.
            return context.Mode == CalendarMode.Provider ? WithResponse(result, response) : result;
        }
        finally
        {
            AnswerGate.Release();
        }
    }

    public async Task<MailInvitationState> RemoveCancelledEventAsync(MailCopy mailCopy, MimeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mailCopy);
        ArgumentNullException.ThrowIfNull(message);

        var invitation = Parse(message);
        if (invitation == null)
            throw new MailInvitationException(Translator.MailInvitationService_CannotRemove);

        await AnswerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await LoadContextAsync(mailCopy, invitation, cancellationToken).ConfigureAwait(false);
            var state = await BuildStateAsync(context).ConfigureAwait(false);

            if (!state.CanRemoveFromCalendar || context.StoredItem == null || context.Account == null)
                throw new MailInvitationException(Translator.MailInvitationService_CannotRemove);

            try
            {
                if (context.Mode == CalendarMode.CalDav)
                {
                    await _calDavEngine.DeleteEventAsync(context.Account, context.StoredItem, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (context.StoredItem.IsRecurringParent)
                    {
                        var occurrences = await _calendarService.GetRecurringChildrenAsync(context.StoredItem.Id).ConfigureAwait(false);
                        foreach (var occurrence in occurrences ?? [])
                            await _changeProcessor.DeleteCalendarItemAsync(occurrence.Id).ConfigureAwait(false);
                    }

                    await _changeProcessor.DeleteCalendarItemAsync(context.StoredItem.Id).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not MailInvitationException)
            {
                Logger.Warning(ex, "Could not remove the cancelled event {Uid}", invitation.Uid);
                throw new MailInvitationException(string.Format(Translator.MailInvitationService_RemoveFailed, ex.Message), ex);
            }

            var refreshed = await LoadContextAsync(mailCopy, invitation, cancellationToken).ConfigureAwait(false);
            return await BuildStateAsync(refreshed).ConfigureAwait(false);
        }
        finally
        {
            AnswerGate.Release();
        }
    }

    #region State

    private async Task<InvitationContext> LoadContextAsync(MailCopy mailCopy, MailInvitation invitation, CancellationToken cancellationToken)
    {
        var accountId = mailCopy.AssignedAccount?.Id ?? mailCopy.AssignedFolder?.MailAccountId ?? Guid.Empty;
        var account = accountId == Guid.Empty ? null : await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);

        if (account == null)
            return new InvitationContext(invitation, null, CalDavUserIdentity.Resolve(null, null), CalendarMode.Unavailable, new List<string>(), null, null, null, null, string.Empty);

        var mode = ResolveMode(account);
        var aliases = await _accountService.GetAccountAliasesAsync(account.Id).ConfigureAwait(false) ?? [];
        var addresses = aliases
            .Select(alias => alias.AliasAddress)
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Select(address => $"mailto:{address.Trim()}")
            .ToList();

        if (mode == CalendarMode.CalDav)
        {
            var calDavIdentity = await _calDavEngine.GetUserIdentityAsync(account, cancellationToken).ConfigureAwait(false);
            addresses.AddRange(calDavIdentity.EmailAddresses.Select(address => $"mailto:{address}"));
        }

        var identity = CalDavUserIdentity.Resolve(addresses, account.Address);
        var sendingAddresses = aliases
            .Select(alias => alias.AliasAddress?.Trim())
            .OfType<string>()
            .Where(address => address.Length > 0)
            .Append(account.Address)
            .ToList();
        var primaryAddress = aliases.FirstOrDefault(alias => alias.IsPrimary && !string.IsNullOrWhiteSpace(alias.AliasAddress))?.AliasAddress?.Trim()
                             ?? account.Address;
        var calendars = await _calendarService.GetAccountCalendarsAsync(account.Id).ConfigureAwait(false) ?? [];
        var storedItem = await FindStoredItemAsync(account, mailCopy, invitation, mode, calendars).ConfigureAwait(false);

        CalDavResourceSnapshot? snapshot = null;
        if (storedItem != null && mode is CalendarMode.CalDav or CalendarMode.Local)
            snapshot = await _changeProcessor.GetCalendarItemIcsAsync(account.Id, storedItem.CalendarId, storedItem.Id).ConfigureAwait(false);

        var storedEvent = string.IsNullOrWhiteSpace(snapshot?.IcsContent)
            ? null
            : MailInvitationReader.FindStoredEvent(snapshot.IcsContent, invitation.Uid, invitation.RecurrenceId, identity);

        var targetCalendar = storedItem != null
            ? calendars.FirstOrDefault(calendar => calendar.Id == storedItem.CalendarId)
            : SelectTargetCalendar(calendars, mode);

        return new InvitationContext(invitation, account, identity, mode, sendingAddresses, storedItem, snapshot, storedEvent, targetCalendar, primaryAddress);
    }

    private async Task<MailInvitationState> BuildStateAsync(InvitationContext context)
    {
        var invitation = context.Invitation;
        var identity = context.Identity;
        var storedItem = context.StoredItem;

        var storedEvent = context.StoredEvent;

        var isOrganizer = invitation.OrganizerEmail.Length > 0 && identity.IsUser(invitation.OrganizerEmail);

        // RFC 5546 section 3.2: only the event's organizer updates or cancels it. A stored event with
        // another ORGANIZER (or none) is not the event this message describes.
        var isForeign = storedEvent != null && !MailInvitationReader.IsSameAddress(storedEvent.OrganizerEmail, invitation.OrganizerEmail);

        // A request for one occurrence compares with that occurrence's override only; without one it
        // is new to the calendar. A cancellation of an occurrence without one compares with the series.
        var storedSequence = invitation.Method == MailInvitationMethod.Request
            ? storedEvent?.Sequence
            : storedEvent?.Sequence ?? storedEvent?.SeriesSequence;
        var isOutdated = !isForeign && storedSequence > invitation.Sequence;
        var isUpdate = !isForeign &&
                       invitation.Method == MailInvitationMethod.Request &&
                       storedEvent != null &&
                       (storedSequence == null || storedSequence < invitation.Sequence);
        var isCancelled = invitation.Method == MailInvitationMethod.Cancel || MailInvitationReader.IsEventCancelled(invitation.RawIcs);

        // RFC 5546 3.2.2: an organizer's update can reset attendee answers, so the stored answer
        // belongs to the older version and the update asks the user again.
        var currentResponse = isUpdate || isForeign
            ? context.Invitation.Attendees.FirstOrDefault(attendee => context.Identity.IsUser(attendee.Email))?.Status
            : await ResolveCurrentResponseAsync(context, isOrganizer).ConfigureAwait(false);

        var (canRespond, reason) = (false, string.Empty);
        if (isForeign && invitation.Method is MailInvitationMethod.Request or MailInvitationMethod.Cancel)
            reason = Translator.MailInvitationService_NotFromOrganizer;
        else if (invitation.Method == MailInvitationMethod.Request && !isCancelled && !isOrganizer && !isOutdated)
            (canRespond, reason) = ResolveRespondCapability(context);

        var canRemove = invitation.Method == MailInvitationMethod.Cancel &&
                        storedItem != null &&
                        storedEvent != null &&
                        !isForeign &&
                        !isOutdated &&
                        context.Mode is CalendarMode.CalDav or CalendarMode.Local &&
                        context.TargetCalendar is { IsReadOnly: false } &&
                        TargetsCancelledEvent(storedItem, invitation);

        return new MailInvitationState
        {
            Invitation = invitation,
            CurrentResponse = currentResponse,
            CalendarItemId = storedItem?.Id,
            IsOrganizer = isOrganizer,
            IsOutdated = isOutdated,
            IsUpdate = isUpdate,
            IsCancelled = isCancelled,
            CanRespond = canRespond,
            CanRemoveFromCalendar = canRemove,
            UnavailableReason = reason
        };
    }

    private static (bool CanRespond, string Reason) ResolveRespondCapability(InvitationContext context)
    {
        if (context.Invitation.OrganizerEmail.Length == 0)
            return (false, Translator.MailInvitationService_NoOrganizer);

        switch (context.Mode)
        {
            case CalendarMode.Provider:
                return context.StoredItem != null
                    ? (true, string.Empty)
                    : (false, Translator.MailInvitationService_AnswerFromCalendar);
            case CalendarMode.CalDav:
            case CalendarMode.Local:
                if (string.IsNullOrWhiteSpace(context.Account?.ServerInformation?.OutgoingServer))
                    return (false, Translator.MailInvitationService_NoOutgoingServer);

                return context.TargetCalendar is { IsReadOnly: false }
                    ? (true, string.Empty)
                    : (false, Translator.MailInvitationService_NoWritableCalendar);
            default:
                return (false, Translator.MailInvitationService_CalendarUnavailable);
        }
    }

    /// <summary>
    /// The user's own answer: the attendee row of the stored event, else its answered status, else
    /// the PARTSTAT the invitation lists for the user. Null when the user is not an attendee.
    /// </summary>
    private async Task<AttendeeStatus?> ResolveCurrentResponseAsync(InvitationContext context, bool isOrganizer)
    {
        if (context.StoredItem is { } storedItem)
        {
            var attendees = await _calendarService.GetAttendeesAsync(storedItem.Id).ConfigureAwait(false) ?? [];
            var own = attendees.FirstOrDefault(attendee => context.Identity.IsUser(attendee.Email));
            if (own != null)
                return own.AttendenceStatus;

            if (!isOrganizer)
            {
                switch (storedItem.Status)
                {
                    case CalendarItemStatus.Accepted:
                        return AttendeeStatus.Accepted;
                    case CalendarItemStatus.Tentative:
                        return AttendeeStatus.Tentative;
                    case CalendarItemStatus.Cancelled when context.Invitation.Method == MailInvitationMethod.Request:
                        return AttendeeStatus.Declined;
                }
            }
        }

        return context.Invitation.Attendees.FirstOrDefault(attendee => context.Identity.IsUser(attendee.Email))?.Status;
    }

    private async Task<CalendarItem?> FindStoredItemAsync(
        MailAccount account,
        MailCopy mailCopy,
        MailInvitation invitation,
        CalendarMode mode,
        IReadOnlyList<AccountCalendar> calendars)
    {
        var mapping = await _changeProcessor.GetMailInvitationCalendarMappingAsync(account.Id, mailCopy.Id).ConfigureAwait(false);
        if (mapping != null && string.Equals(mapping.InvitationUid, invitation.Uid, StringComparison.Ordinal))
        {
            var mapped = await _calendarService.GetCalendarItemAsync(mapping.CalendarItemId).ConfigureAwait(false);
            if (mapped != null)
                return mapped;
        }

        if (mode is not (CalendarMode.CalDav or CalendarMode.Local))
            return null;

        foreach (var remoteEventId in GetRemoteEventIds(invitation))
        {
            foreach (var calendar in calendars)
            {
                var item = await _calendarService.GetCalendarItemAsync(calendar.Id, remoteEventId).ConfigureAwait(false);
                if (item != null)
                    return item;
            }
        }

        return null;
    }

    /// <summary>The stored ID of the invited occurrence, then of its series (CalDAV IDs are "UID" or "UID::occurrence").</summary>
    private static IEnumerable<string> GetRemoteEventIds(MailInvitation invitation)
    {
        if (!string.IsNullOrEmpty(invitation.RecurrenceId))
            yield return GetOccurrenceRemoteEventId(invitation);

        yield return invitation.Uid;
    }

    private static string GetOccurrenceRemoteEventId(MailInvitation invitation)
        => string.IsNullOrEmpty(invitation.RecurrenceId) ? invitation.Uid : $"{invitation.Uid}::{invitation.RecurrenceId}";

    /// <summary>A cancelled occurrence can be removed only when its own row was found, not just its series.</summary>
    private static bool TargetsCancelledEvent(CalendarItem storedItem, MailInvitation invitation)
        => string.IsNullOrEmpty(invitation.RecurrenceId) || string.Equals(storedItem.RemoteEventId, GetOccurrenceRemoteEventId(invitation), StringComparison.Ordinal);

    private static CalendarMode ResolveMode(MailAccount account)
    {
        if (account.ProviderType is MailProviderType.Outlook or MailProviderType.Gmail)
            return CalendarMode.Provider;

        var source = account.GetEffectiveCalendarIntegrationSource();
        if (source == AccountIntegrationSource.Local)
            return CalendarMode.Local;

        if (source == AccountIntegrationSource.Dav &&
            account.ProviderType == MailProviderType.IMAP4 &&
            account.IsCalendarAccessGranted &&
            account.ServerInformation?.CalendarSupportMode == ImapCalendarSupportMode.CalDav)
        {
            return CalendarMode.CalDav;
        }

        return CalendarMode.Unavailable;
    }

    /// <summary>The calendar a new invitation goes to: the default calendar for new events when it is this account's, else the primary one, else the first writable one.</summary>
    private AccountCalendar? SelectTargetCalendar(IReadOnlyList<AccountCalendar> calendars, CalendarMode mode)
    {
        var writable = calendars
            .Where(calendar => !calendar.IsReadOnly)
            .Where(calendar => mode != CalendarMode.CalDav || !string.IsNullOrWhiteSpace(calendar.RemoteCalendarId))
            .ToList();

        var defaultCalendarId = _preferencesService.DefaultNewEventCalendarId;

        return writable.FirstOrDefault(calendar => calendar.Id == defaultCalendarId)
               ?? writable.FirstOrDefault(calendar => calendar.IsPrimary)
               ?? writable.FirstOrDefault(calendar => calendar.IsSynchronizationEnabled)
               ?? writable.FirstOrDefault();
    }

    /// <summary>
    /// Mail synced before invitations were recognized is stored as plain mail. The calendar part
    /// decides, by the rule synchronization uses. Only IMAP and POP3 messages are turned back into
    /// plain mail; other accounts get their types from the provider. A list row already shown keeps
    /// its template until the list realizes it again.
    /// </summary>
    private async Task CorrectItemTypeAsync(MailCopy mailCopy, MimeMessage message)
    {
        var expected = MailInvitationReader.GetItemType(message);
        var isCustomAccount = mailCopy.AssignedAccount?.ProviderType is MailProviderType.IMAP4 or MailProviderType.POP3;

        if (expected == MailItemType.Mail && (!isCustomAccount || mailCopy.ItemType == MailItemType.Mail))
            return;

        try
        {
            var stored = mailCopy.UniqueId == Guid.Empty ? null : await _mailService.GetSingleMailItemAsync(mailCopy.UniqueId).ConfigureAwait(false);
            mailCopy.ItemType = expected;

            if (stored == null || stored.ItemType == expected)
                return;

            stored.ItemType = expected;
            await _mailService.UpdateMailAsync(stored).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not correct the item type of mail {MailCopyId}", mailCopy.Id);
        }
    }

    /// <summary>The state after an answer the provider has queued: answered, so no longer an unanswered update.</summary>
    private static MailInvitationState WithResponse(MailInvitationState state, AttendeeStatus response)
        => new()
        {
            Invitation = state.Invitation,
            CurrentResponse = response,
            CalendarItemId = state.CalendarItemId,
            IsOrganizer = state.IsOrganizer,
            IsOutdated = state.IsOutdated,
            IsUpdate = false,
            IsCancelled = state.IsCancelled,
            CanRespond = state.CanRespond,
            CanRemoveFromCalendar = state.CanRemoveFromCalendar,
            UnavailableReason = state.UnavailableReason
        };

    #endregion

    #region Answers

    private async Task RespondThroughProviderAsync(InvitationContext context, AttendeeStatus response, string? comment)
    {
        var item = context.StoredItem ?? throw new MailInvitationException(Translator.MailInvitationService_AnswerFromCalendar);
        var operation = response switch
        {
            AttendeeStatus.Accepted => CalendarSynchronizerOperation.AcceptEvent,
            AttendeeStatus.Tentative => CalendarSynchronizerOperation.TentativeEvent,
            _ => CalendarSynchronizerOperation.DeclineEvent
        };

        var request = string.IsNullOrWhiteSpace(comment)
            ? new CalendarOperationPreparationRequest(operation, item)
            : new CalendarOperationPreparationRequest(operation, item, ResponseMessage: comment.Trim());

        await _requestDelegator.ExecuteAsync(request).ConfigureAwait(false);
    }

    /// <summary>
    /// CalDAV first, then the reply: a failed write sends nothing. When the reply fails after the
    /// write, the write is undone; only when that fails too does the error say the calendar already
    /// has the event. Nothing is stored locally until the reply is sent.
    /// </summary>
    private async Task RespondWithCalDavAsync(InvitationContext context, MailCopy mailCopy, AttendeeStatus response, string? comment, CancellationToken cancellationToken)
    {
        if (context.Account is not { } account || context.TargetCalendar is not { } calendar)
            throw new MailInvitationException(Translator.MailInvitationService_CalendarUnavailable);

        var invitation = context.Invitation;
        var (address, name) = ResolveAttendee(context, account);
        var reply = CreateReplyMessage(context, account, address, name, response, comment);

        CalDavResourceSnapshot? previous;
        CalDavResourceSnapshot written;
        try
        {
            (previous, written) = await _calDavEngine.WriteInvitationCopyAsync(
                account,
                calendar.Id,
                context.StoredItem,
                invitation.Uid,
                storedIcs => CreateStoredCopy(context, storedIcs, address, name, response),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DavRequestException ex) when (context.StoredItem == null && IsExistingResourceConflict(ex))
        {
            Logger.Information(ex, "Invitation {Uid} already exists in CalDAV calendar {CalendarId}", invitation.Uid, calendar.Id);
            throw new MailInvitationException(Translator.MailInvitationService_AlreadyInCalendar, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MailInvitationException)
        {
            Logger.Warning(ex, "Could not write invitation {Uid} to CalDAV calendar {CalendarId}", invitation.Uid, calendar.Id);
            throw new MailInvitationException(string.Format(Translator.MailInvitationService_CalendarWriteFailed, ex.Message), ex);
        }

        try
        {
            await _smtpTransport.SendAsync(account, reply, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "The reply to invitation {Uid} could not be sent after the calendar write; undoing the write", invitation.Uid);

            try
            {
                await _calDavEngine.RevertInvitationCopyAsync(account, calendar.Id, context.StoredItem, previous, written, invitation.Uid, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception revertException)
            {
                Logger.Warning(revertException, "Could not undo the calendar write of invitation {Uid}", invitation.Uid);
                throw new MailInvitationException(string.Format(Translator.MailInvitationService_ReplyFailedAfterCalendar, ex.Message), ex);
            }

            throw new MailInvitationException(string.Format(Translator.MailInvitationService_ReplyFailed, ex.Message), ex);
        }

        try
        {
            await _calDavEngine.ApplyInvitationCopyAsync(account, calendar.Id, context.StoredItem, written, cancellationToken).ConfigureAwait(false);
            await PersistAnswerAsync(context, account, calendar.Id, mailCopy, response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The server has the event and the organizer has the answer; the next sync stores it.
            Logger.Warning(ex, "Could not store the answered invitation {Uid} locally", invitation.Uid);
        }
    }

    /// <summary>
    /// A create refused because the resource or its UID exists already: If-None-Match failed, or the
    /// CalDAV preconditions on unique UIDs (RFC 4791 section 5.3.2.1, RFC 6638 section 3.2.4.1).
    /// </summary>
    private static bool IsExistingResourceConflict(DavRequestException exception)
        => exception.StatusCode == 412 ||
           exception.HasError("no-uid-conflict") ||
           exception.HasError("unique-scheduling-object-resource");

    /// <summary>
    /// The reply goes first: the local calendar is Wino's own, so the organizer is the only server to
    /// confirm. An answer the local calendar already holds for this version is not sent again, so a
    /// retry after a failed local step does not mail the organizer twice.
    /// </summary>
    private async Task RespondWithLocalCalendarAsync(InvitationContext context, MailCopy mailCopy, AttendeeStatus response, string? comment, CancellationToken cancellationToken)
    {
        if (context.Account is not { } account || context.TargetCalendar is not { } calendar)
            throw new MailInvitationException(Translator.MailInvitationService_CalendarUnavailable);

        var invitation = context.Invitation;
        var (address, name) = ResolveAttendee(context, account);
        var icsContent = CreateStoredCopy(context, context.Snapshot?.IcsContent, address, name, response);
        var isAnswerStored = context.StoredEvent is { } stored &&
                             stored.Sequence == invitation.Sequence &&
                             stored.OwnStatus == response;

        if (!isAnswerStored)
        {
            var reply = CreateReplyMessage(context, account, address, name, response, comment);

            try
            {
                await _smtpTransport.SendAsync(account, reply, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning(ex, "The reply to invitation {Uid} could not be sent", invitation.Uid);
                throw new MailInvitationException(string.Format(Translator.MailInvitationService_ReplyFailed, ex.Message), ex);
            }
        }

        try
        {
            await StoreLocalCopyAsync(context, account, calendar, icsContent).ConfigureAwait(false);
            await PersistAnswerAsync(context, account, calendar.Id, mailCopy, response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Could not add the answered invitation {Uid} to the local calendar", invitation.Uid);
            throw new MailInvitationException(string.Format(Translator.MailInvitationService_LocalCalendarFailed, ex.Message), ex);
        }
    }

    /// <summary>
    /// Stores the copy the way a CalDAV synchronization stores a resource: every row it expands to,
    /// and the rows of the same object it no longer expands to are removed. The window reaches the
    /// invited event itself, so an event far from today is still stored.
    /// </summary>
    private async Task StoreLocalCopyAsync(InvitationContext context, MailAccount account, AccountCalendar calendar, string icsContent)
    {
        var invitation = context.Invitation;
        var existingHref = context.Snapshot?.ExactHref;
        var href = string.IsNullOrWhiteSpace(existingHref) ? $"local://calendar/{calendar.Id:N}/{Guid.NewGuid():N}" : existingHref;
        var eTag = DateTimeOffset.UtcNow.ToString("O");

        var now = DateTimeOffset.UtcNow;
        var periodStart = now.AddYears(-1);
        var periodEnd = now.AddYears(2);
        if (!invitation.IsRecurring && invitation.Start is { } start)
        {
            var end = invitation.End ?? start;
            periodStart = start.AddDays(-1) < periodStart ? start.AddDays(-1) : periodStart;
            periodEnd = end.AddDays(1) > periodEnd ? end.AddDays(1) : periodEnd;
        }

        var events = _calDavClient.ExpandCalendarResource(icsContent, href, eTag, periodStart, periodEnd)
                     ?? Array.Empty<CalDavCalendarEvent>();
        var expandedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var calendarEvent in events)
        {
            if (string.IsNullOrWhiteSpace(calendarEvent.RemoteEventId))
                continue;

            expandedIds.Add(calendarEvent.RemoteEventId);
            CalDavSynchronizationEngine.ApplyImportedResponseStatus(calendarEvent, context.Identity);
            await _changeProcessor.ManageCalendarEventAsync(calendarEvent, calendar, account).ConfigureAwait(false);

            var item = await _calendarService.GetCalendarItemAsync(calendar.Id, calendarEvent.RemoteEventId).ConfigureAwait(false);
            if (item != null)
                await _changeProcessor.SaveCalendarItemIcsAsync(account.Id, calendar.Id, item.Id, calendarEvent.RemoteEventId, href, eTag, icsContent).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(existingHref))
            return;

        var rows = await _changeProcessor.GetCalendarIcsShadowsAsync(account.Id, calendar.Id).ConfigureAwait(false)
                   ?? Array.Empty<CalendarIcsShadow>();

        foreach (var row in rows)
        {
            if (string.Equals(row.ExactHref, existingHref, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(row.RemoteEventId) &&
                !expandedIds.Contains(row.RemoteEventId))
            {
                await _changeProcessor.DeleteCalendarItemAsync(row.CalendarItemId).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sets the answer on the stored rows and links the mail to the event.</summary>
    private async Task PersistAnswerAsync(InvitationContext context, MailAccount account, Guid calendarId, MailCopy mailCopy, AttendeeStatus response)
    {
        CalendarItem? item = null;
        foreach (var remoteEventId in GetRemoteEventIds(context.Invitation))
        {
            item = await _calendarService.GetCalendarItemAsync(calendarId, remoteEventId).ConfigureAwait(false);
            if (item != null)
                break;
        }

        if (item == null)
            return;

        await _calDavEngine.PersistInvitationResponseAsync(account, item, context.Identity, response).ConfigureAwait(false);
        await _changeProcessor.UpsertMailInvitationCalendarMappingAsync(new MailInvitationCalendarMapping
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            MailCopyId = mailCopy.Id,
            InvitationUid = context.Invitation.Uid,
            CalendarId = calendarId,
            CalendarItemId = item.Id,
            CalendarRemoteEventId = item.RemoteEventId,
            UpdatedAtUtc = DateTime.UtcNow
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The attendee's copy to store (see <see cref="MailInvitationIcs.CreateAnsweredCopy"/>). The stored
    /// copy is checked again here because a CalDAV write reads it from the server: a stored event
    /// another organizer owns is never replaced.
    /// </summary>
    private static string CreateStoredCopy(
        InvitationContext context,
        string? storedIcs,
        string address,
        string name,
        AttendeeStatus response)
    {
        var invitation = context.Invitation;

        if (!string.IsNullOrWhiteSpace(storedIcs) &&
            MailInvitationReader.FindStoredEvent(storedIcs, invitation.Uid, invitation.RecurrenceId, context.Identity) is { } stored &&
            !MailInvitationReader.IsSameAddress(stored.OrganizerEmail, invitation.OrganizerEmail))
        {
            throw new MailInvitationException(Translator.MailInvitationService_NotFromOrganizer);
        }

        return MailInvitationIcs.CreateAnsweredCopy(storedIcs, invitation.RawIcs, invitation.Uid, invitation.RecurrenceId, context.Identity, address, name, response);
    }

    /// <summary>
    /// The address the user answers from: the invited address when the account can send as it,
    /// else the primary alias or the account address. The reply's ATTENDEE and the stored copy keep
    /// the invited calendar address when the invitation lists the user; otherwise (a forwarded
    /// invitation) both add this address.
    /// </summary>
    private static (string Address, string Name) ResolveAttendee(InvitationContext context, MailAccount account)
    {
        var name = string.IsNullOrWhiteSpace(account.SenderName) ? account.Address : account.SenderName;

        var invited = context.Invitation.Attendees
            .Select(attendee => attendee.Email)
            .FirstOrDefault(email => context.SendingAddresses.Contains(email, StringComparer.OrdinalIgnoreCase));

        return (string.IsNullOrWhiteSpace(invited) ? context.PrimaryAddress : invited, name);
    }

    /// <summary>An iMIP REPLY (RFC 6047 section 2.4): a short text and the text/calendar REPLY as alternatives, to the organizer.</summary>
    private static MimeMessage CreateReplyMessage(
        InvitationContext context,
        MailAccount account,
        string address,
        string name,
        AttendeeStatus response,
        string? comment)
    {
        var invitation = context.Invitation;
        var icsContent = MailInvitationIcs.CreateReply(invitation.RawIcs, invitation.Uid, context.Identity, address, name, response, comment);

        var (subjectFormat, bodyFormat) = response switch
        {
            AttendeeStatus.Accepted => (Translator.MailInvitationService_ReplySubjectAccepted, Translator.MailInvitationService_ReplyBodyAccepted),
            AttendeeStatus.Tentative => (Translator.MailInvitationService_ReplySubjectTentative, Translator.MailInvitationService_ReplyBodyTentative),
            _ => (Translator.MailInvitationService_ReplySubjectDeclined, Translator.MailInvitationService_ReplyBodyDeclined)
        };

        var text = string.Format(bodyFormat, name);
        if (!string.IsNullOrWhiteSpace(comment))
            text += Environment.NewLine + Environment.NewLine + comment.Trim();

        var calendarPart = new TextPart("calendar") { ContentTransferEncoding = ContentEncoding.Base64 };
        calendarPart.SetText(Encoding.UTF8, icsContent);
        calendarPart.ContentType.Parameters.Add("method", "REPLY");

        var textPart = new TextPart("plain");
        textPart.SetText(Encoding.UTF8, text);

        var message = new MimeMessage
        {
            Subject = string.Format(subjectFormat, invitation.Summary),
            Body = new MultipartAlternative { textPart, calendarPart }
        };

        message.From.Add(new MailboxAddress(name, address));
        message.To.Add(new MailboxAddress(invitation.OrganizerName, invitation.OrganizerEmail));

        return message;
    }

    #endregion

    private enum CalendarMode
    {
        Unavailable,
        Provider,
        CalDav,
        Local
    }

    private sealed record InvitationContext(
        MailInvitation Invitation,
        MailAccount? Account,
        CalDavUserIdentity Identity,
        CalendarMode Mode,
        IReadOnlyList<string> SendingAddresses,
        CalendarItem? StoredItem,
        CalDavResourceSnapshot? Snapshot,
        StoredInvitationEvent? StoredEvent,
        AccountCalendar? TargetCalendar,
        string PrimaryAddress);
}
