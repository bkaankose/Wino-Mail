using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Providers.Exchange.Synchronizers.Exchange.ActiveSync;

/// <summary>
/// One calendar write: a series or single event, or one occurrence of a series when <see cref="InstanceKey"/> is set.
/// <see cref="ChangesAttachments"/> marks data that adds or deletes attachments (MS-ASAIRS Add and Delete, 16.x).
/// </summary>
internal sealed record EasEventMutation(string Operation, string RemoteId, XElement? Data, string ClientId = "", string? InstanceKey = null,
    Func<string, Task>? Persist = null, bool ChangesAttachments = false);

internal sealed partial class EasSynchronizationEngine
{
    private DateTime _seriesExpandedOn;

    /// <summary>
    /// Stores an event read from the server: a single event, or a series master (stored, not shown) and its
    /// occurrences in the calendar window, exceptions applied. Occurrences the series no longer has are removed.
    /// </summary>
    private async Task ProjectEventAsync(EasCollection state, string remoteId, XElement data)
    {
        var calendar = await _calendars.GetAccountCalendarAsync(state.Id).ConfigureAwait(false) ?? throw new InvalidOperationException("The EAS calendar is missing.");
        var item = EasDataMapper.ReadEvent(data, calendar, remoteId, _account.Address);
        if (item == null)
            return;

        var storedEvent = await _calendars.GetCalendarItemAsync(calendar.Id, remoteId).ConfigureAwait(false);
        var trackingId = storedEvent?.RemoteEventId.GetClientTrackingId();
        // 14.x echoes the UID the client sent, which is the local event id.
        if (EasDataMapper.ClientEventId(data) is Guid clientId)
        {
            var local = await _database.Connection.FindAsync<CalendarItem>(clientId).ConfigureAwait(false);
            if (local?.CalendarId == calendar.Id)
            {
                if (storedEvent != null && storedEvent.Id != local.Id)
                    await _calendars.DeleteCalendarItemAsync(storedEvent.Id).ConfigureAwait(false);
                storedEvent = local;
                trackingId = clientId;
            }
        }
        if (storedEvent != null)
            item.Id = storedEvent.Id;
        item.RemoteEventId = remoteId.WithClientTrackingId(trackingId);
        await UpsertEventAsync(item, data, storedEvent != null).ConfigureAwait(false);

        var children = await _calendars.GetRecurringChildrenAsync(item.Id).ConfigureAwait(false);
        var occurrences = SeriesOccurrences(item, data);
        foreach (var occurrence in occurrences)
            await UpsertEventAsync(occurrence.Item, occurrence.Data, children.Any(child => child.Id == occurrence.Item.Id)).ConfigureAwait(false);
        var expected = occurrences.Select(occurrence => occurrence.Item.Id).ToHashSet();
        foreach (var stale in children.Where(child => !expected.Contains(child.Id)))
            await _calendars.DeleteCalendarItemAsync(stale.Id).ConfigureAwait(false);
    }

    private List<EasOccurrence> SeriesOccurrences(CalendarItem master, XElement data)
    {
        var now = DateTime.UtcNow;
        return EasDataMapper.ExpandEvent(data, master, _account.Address, now - EasDataMapper.CalendarWindowPast, now + EasDataMapper.CalendarWindowFuture);
    }

    private async Task UpsertEventAsync(CalendarItem item, XElement data, bool exists)
    {
        var attendees = EasDataMapper.ReadAttendees(data, item.Id);
        if (exists)
            await _calendars.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
        else
            await _calendars.CreateNewCalendarItemAsync(item, attendees).ConfigureAwait(false);

        await ExchangeCalendarAttachments.ReconcileAsync(_database.Connection, item.Id, EasDataMapper.ReadAttachments(data)).ConfigureAwait(false);

        List<Reminder> server = EasDataMapper.ReadReminderMinutes(data) is { } minutes
            ? [new Reminder { Id = EasDataMapper.StableId(item.Id.ToString("N") + "/reminder"), CalendarItemId = item.Id, DurationInSeconds = minutes * 60L }]
            : [];
        // The server keeps one reminder; the ones kept only in Wino stay.
        var reminders = exists
            ? ReminderSyncPlan.Reconcile(await _calendars.GetRemindersAsync(item.Id).ConfigureAwait(false), server, CalendarCapabilities.Exchange, item.Id)
            : server;
        if (reminders != null)
            await _calendars.SaveRemindersAsync(item.Id, reminders).ConfigureAwait(false);
    }

    /// <summary>
    /// Once a day, adds the occurrences of stored series that moved into the calendar window and removes the ones in
    /// the window a series no longer has. A series the server has not changed is not reported again, so its window
    /// would otherwise stay where the last change left it.
    /// </summary>
    private async Task ExtendSeriesWindowAsync(IEnumerable<EasCollectionState> calendarStates)
    {
        var today = DateTime.UtcNow.Date;
        if (_seriesExpandedOn == today)
            return;

        foreach (var state in calendarStates)
        {
            var calendar = await _calendars.GetAccountCalendarAsync(state.Id).ConfigureAwait(false);
            if (calendar == null)
                continue;
            var shadows = await _database.Connection.Table<EasItemShadow>().Where(shadow => shadow.CollectionId == state.Id).ToListAsync().ConfigureAwait(false);
            foreach (var shadow in shadows.Where(shadow => shadow.ApplicationData.Contains("Recurrence", StringComparison.Ordinal)))
            {
                try
                {
                    var data = Parse(shadow.ApplicationData);
                    var stored = await _calendars.GetCalendarItemAsync(calendar.Id, shadow.RemoteId).ConfigureAwait(false);
                    if (stored == null || EasDataMapper.ReadEvent(data, calendar, shadow.RemoteId, _account.Address) is not { } master)
                    {
                        await ProjectEventAsync(EasCollection.From(state), shadow.RemoteId, data).ConfigureAwait(false);
                        continue;
                    }
                    master.Id = stored.Id;
                    master.RemoteEventId = stored.RemoteEventId;
                    var children = (await _calendars.GetRecurringChildrenAsync(master.Id).ConfigureAwait(false)).ToDictionary(child => child.Id);
                    var occurrences = SeriesOccurrences(master, data);
                    foreach (var occurrence in occurrences.Where(occurrence => !children.ContainsKey(occurrence.Item.Id)))
                        await UpsertEventAsync(occurrence.Item, occurrence.Data, false).ConfigureAwait(false);
                    var expected = occurrences.Select(occurrence => occurrence.Item.Id).ToHashSet();
                    var windowStart = DateTime.UtcNow - EasDataMapper.CalendarWindowPast;
                    foreach (var child in children.Values.Where(child => !expected.Contains(child.Id) && child.StartDate >= windowStart.AddDays(1)))
                        await _calendars.DeleteCalendarItemAsync(child.Id).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsItemFailure(exception, CancellationToken.None))
                {
                    _itemFailed(EasCollection.From(state), shadow.RemoteId, exception);
                }
            }
        }
        _seriesExpandedOn = today;
    }

    /// <summary>
    /// Adds, changes or deletes an event, or one occurrence of a series. A whole-event Change sends the stored server
    /// copy merged with the edit, because EAS clears fields a Change leaves out. An occurrence is addressed by
    /// InstanceId on 16.x and through the series' Exceptions on 14.x. The server does not report a client's own change
    /// back, so the stored copy is updated here and the event projected again from it.
    /// </summary>
    internal async Task<string> MutateEventAsync(string folderId, EasEventMutation mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await GetCollectionAsync(folderId).ConfigureAwait(false);
            await SynchronizeCollectionAsync(state, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
            var sync = EasDataMapper.Sync;
            var isProtocol16 = _client.IsAtLeast("16.0");
            var operation = mutation.Operation;
            var shadowKey = EasDataMapper.Key(_account.Id, folderId, mutation.RemoteId);
            var shadow = operation == "Add" ? null : await _database.Connection.FindAsync<EasItemShadow>(shadowKey).ConfigureAwait(false);
            var stored = shadow == null ? null : Parse(shadow.ApplicationData);
            XElement? updated = null;
            var command = new XElement(sync + operation, new XElement(sync + (operation == "Add" ? "ClientId" : "ServerId"), operation == "Add" ? mutation.ClientId : mutation.RemoteId));

            if (mutation.InstanceKey is { } instanceKey)
            {
                if (stored == null)
                    throw new InvalidOperationException(Wino.Core.Domain.Translator.ExchangeSetup_ActiveSyncItemRequiresSync);
                // On 16.x the edit holds only the changed elements; the stored exception keeps the others.
                var exceptionData = operation == "Delete" || mutation.Data == null ? null
                    : isProtocol16 ? Merge(StoredException(stored, instanceKey)?.ToString(SaveOptions.DisableFormatting), mutation.Data) : mutation.Data;
                var exception = OccurrenceException(instanceKey, exceptionData, isProtocol16);
                updated = WithException(stored, exception, instanceKey);
                if (isProtocol16)
                {
                    command.Add(new XElement(EasDataMapper.Body + "InstanceId", instanceKey));
                    if (operation == "Change" && mutation.Data != null)
                        command.Add(EasDataMapper.ForEventChange(OccurrenceData(mutation.Data), true));
                }
                else
                {
                    // 14.x has no instance addressing: the series is changed with the exception added to its list.
                    command = new XElement(sync + "Change", new XElement(sync + "ServerId", mutation.RemoteId), EasDataMapper.ForEventChange(updated, false));
                }
            }
            else if (mutation.Data != null)
            {
                updated = operation == "Add" ? new XElement(mutation.Data) : Merge(shadow?.ApplicationData, mutation.Data);
                // 16.x ghosts calendar elements: a Change carries only the changed ones (the edit), 14.x the whole event.
                var change = EasDataMapper.ForEventChange(isProtocol16 ? mutation.Data : updated, isProtocol16);
                // Only the organizer sends the attendee list; an attendee's copy keeps the server's.
                if (EasDataMapper.Value(updated, EasDataMapper.Calendar, "OrganizerEmail") is { Length: > 0 } organizer &&
                    !string.Equals(organizer, _account.Address, StringComparison.OrdinalIgnoreCase))
                    change.Elements(EasDataMapper.Calendar + "Attendees").Remove();
                command.Add(operation == "Add" ? mutation.Data : change);
            }

            // The stored copy takes the attachments the server reports after the write, not the Add and Delete instructions.
            if (mutation.ChangesAttachments && updated != null)
                RemoveAttachmentInstructions(updated);

            if (isProtocol16 && operation == "Change" && command.Element(sync + "ApplicationData") is { HasElements: false })
            {
                // Nothing the server holds changed.
                if (mutation.Persist != null)
                    await mutation.Persist(mutation.RemoteId).ConfigureAwait(false);
                return mutation.RemoteId;
            }

            var responseName = command.Name.LocalName;
            XElement? result = null;
            var response = operation == "Add" ? await TakeAnswerAsync(state, mutation.ClientId).ConfigureAwait(false) : null;
            if (response == null)
            {
                // What the stored copy becomes if this request has to be sent again after its response was lost.
                var effects = operation == "Add" ? null
                    : new[] { new EasCommandEffect(mutation.RemoteId, operation == "Delete" && mutation.InstanceKey == null, updated) };
                result = await SendCommandsAsync(state, new XElement(sync + "Commands", command), cancellationToken, effects: effects).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The EAS collection is not synchronized yet.");
                // The server has moved to the new key even when the item itself failed.
                await SaveKeyAsync(state, result).ConfigureAwait(false);
                response = result.Element(sync + "Responses")?.Element(sync + responseName);
            }
            if (response != null)
                RequireItemSuccess(response);
            else if (operation == "Add")
                throw new InvalidDataException("The EAS addition response is missing.");
            var resultId = response?.Element(sync + "ServerId")?.Value ?? mutation.RemoteId;
            if (string.IsNullOrEmpty(resultId))
                throw new InvalidDataException("The EAS mutation response has no item identifier.");
            // 16.x answers an Add with the UID it generated.
            if (updated != null && response?.Element(sync + "ApplicationData") is { } echoed)
                updated = Merge(updated.ToString(SaveOptions.DisableFormatting), echoed);
            // The Sync response names an added attachment only by ClientId and FileReference (MS-ASAIRS 2.2.2.7), so the
            // event's attachments, and for an occurrence its exceptions, are read back from the server.
            if (mutation.ChangesAttachments && updated != null)
                await ApplyServerAttachmentsAsync(folderId, resultId, updated, mutation.InstanceKey != null, cancellationToken).ConfigureAwait(false);

            if (mutation.Persist != null)
                await mutation.Persist(resultId).ConfigureAwait(false);
            if (result != null)
            {
                var unknown = await ApplyPageAsync(state, result, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
                await FetchUnknownAsync(state, unknown, new List<MailCopy>(), cancellationToken).ConfigureAwait(false);
            }

            var key = EasDataMapper.Key(_account.Id, folderId, resultId);
            if (operation == "Delete" && mutation.InstanceKey == null)
            {
                await _database.Connection.DeleteAsync<EasItemShadow>(key).ConfigureAwait(false);
                await DeleteProjectionAsync(state, resultId).ConfigureAwait(false);
            }
            else if (updated != null)
            {
                await _database.Connection.InsertOrReplaceAsync(new EasItemShadow { Id = key, AccountId = _account.Id,
                    CollectionId = state.Id, RemoteId = resultId, ApplicationData = updated.ToString(SaveOptions.DisableFormatting),
                    Generation = state.Generation }, typeof(EasItemShadow)).ConfigureAwait(false);
                await ProjectEventAsync(state, resultId, updated).ConfigureAwait(false);
            }
            return resultId;
        }
        finally { _gate.Release(); }
    }

    private static void RemoveAttachmentInstructions(XElement data)
    {
        var body = EasDataMapper.Body;
        data.DescendantsAndSelf(body + "Attachments")
            .Where(attachments => attachments.Elements(body + "Add").Any() || attachments.Elements(body + "Delete").Any())
            .ToList()
            .ForEach(attachments => attachments.Remove());
    }

    /// <summary>
    /// Replaces the stored copy's attachments with the server's, read with ItemOperations Fetch (MS-ASCMD 2.2.1.10). For
    /// an occurrence the exceptions are replaced too, since an occurrence's attachments are on its exception.
    /// </summary>
    private async Task ApplyServerAttachmentsAsync(string folderId, string serverId, XElement updated, bool replaceExceptions, CancellationToken cancellationToken)
    {
        XNamespace items = "ItemOperations:";
        var response = await _client.CommandAsync("ItemOperations", new XElement(items + "ItemOperations",
            new XElement(items + "Fetch", new XElement(items + "Store", "Mailbox"),
                new XElement(EasDataMapper.Sync + "CollectionId", folderId), new XElement(EasDataMapper.Sync + "ServerId", serverId),
                new XElement(items + "Options", new XElement(EasDataMapper.Body + "BodyPreference", new XElement(EasDataMapper.Body + "Type", "1"), new XElement(EasDataMapper.Body + "TruncationSize", "0"))))),
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The EAS ItemOperations response is missing.");
        EasClient.RequireSuccess(response);
        var fetch = response.Element(items + "Response")?.Element(items + "Fetch") ?? throw new InvalidDataException("The EAS ItemOperations response has no Fetch.");
        EasClient.RequireSuccess(fetch);
        var properties = fetch.Element(items + "Properties") ?? throw new InvalidDataException("The EAS ItemOperations response has no Properties.");

        updated.Elements(EasDataMapper.Body + "Attachments").Remove();
        if (properties.Element(EasDataMapper.Body + "Attachments") is { } attachments)
            updated.Add(new XElement(attachments));

        if (replaceExceptions && properties.Element(EasDataMapper.Calendar + "Exceptions") is { } exceptions)
        {
            updated.Elements(EasDataMapper.Calendar + "Exceptions").Remove();
            updated.Add(new XElement(exceptions));
        }
    }

    /// <summary>
    /// The content of an attachment, fetched by its FileReference with ItemOperations (MS-ASCMD 2.2.1.10, Data
    /// 2.2.3.39.2: base64). The device policy's attachment rules (MS-ASPROV AttachmentsEnabled, MaxAttachmentSize) apply.
    /// </summary>
    internal async Task<byte[]> FetchAttachmentContentAsync(string fileReference, CancellationToken cancellationToken)
    {
        if (_client.PolicyLimits.AttachmentsDisabled)
            throw new InvalidOperationException("The ActiveSync policy of this account does not allow attachments.");

        XNamespace items = "ItemOperations:";
        var response = await _client.CommandAsync("ItemOperations", new XElement(items + "ItemOperations",
            new XElement(items + "Fetch", new XElement(items + "Store", "Mailbox"), new XElement(EasDataMapper.Body + "FileReference", fileReference))),
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The EAS ItemOperations response is missing.");
        EasClient.RequireSuccess(response);
        var fetch = response.Element(items + "Response")?.Element(items + "Fetch") ?? throw new InvalidDataException("The EAS ItemOperations response has no Fetch.");
        EasClient.RequireSuccess(fetch);
        var data = fetch.Element(items + "Properties")?.Element(items + "Data") ?? throw new InvalidDataException("The EAS attachment response has no Data.");
        var content = Convert.FromBase64String(data.Value);

        if (_client.PolicyLimits.MaxAttachmentSize is { } maximum && content.LongLength > maximum)
            throw new InvalidOperationException("The attachment is larger than the ActiveSync policy of this account allows.");
        return content;
    }

    /// <summary>The TimeZone element of the server's copy of an event, as stored; null when there is none.</summary>
    internal async Task<string?> GetStoredTimeZoneAsync(string folderId, string remoteId)
    {
        var shadow = await _database.Connection.FindAsync<EasItemShadow>(EasDataMapper.Key(_account.Id, folderId, remoteId)).ConfigureAwait(false);
        var value = shadow == null ? null : EasDataMapper.Value(Parse(shadow.ApplicationData), EasDataMapper.Calendar, "TimeZone");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>What an occurrence change may carry: no zone, series rule or attendee list of its own.</summary>
    private static XElement OccurrenceData(XElement data)
    {
        var result = new XElement(data);
        result.Elements().Where(element => element.Name == EasDataMapper.Calendar + "TimeZone" || element.Name == EasDataMapper.Calendar + "Recurrence" ||
            element.Name == EasDataMapper.Calendar + "Exceptions" || element.Name == EasDataMapper.Calendar + "UID" || element.Name == EasDataMapper.Calendar + "ClientUid").Remove();
        return result;
    }

    private static XElement OccurrenceException(string instanceKey, XElement? data, bool isProtocol16)
    {
        var calendar = EasDataMapper.Calendar;
        var exception = new XElement(calendar + "Exception",
            isProtocol16 ? new XElement(EasDataMapper.Body + "InstanceId", instanceKey) : new XElement(calendar + "ExceptionStartTime", instanceKey));
        if (data == null)
            exception.AddFirst(new XElement(calendar + "Deleted", "1"));
        else
            exception.Add(OccurrenceData(data).Elements().Where(element => element.Name != calendar + "Attendees" || isProtocol16).Select(element => new XElement(element)));
        return exception;
    }

    /// <summary>The series' stored exception for the occurrence that originally started at <paramref name="instanceKey"/>, without its key.</summary>
    private static XElement? StoredException(XElement series, string instanceKey)
    {
        var calendar = EasDataMapper.Calendar;
        var original = EasDataMapper.Date(instanceKey);
        var existing = series.Element(calendar + "Exceptions")?.Elements(calendar + "Exception").FirstOrDefault(item =>
            EasDataMapper.Date(EasDataMapper.Value(item, calendar, "ExceptionStartTime") is { Length: > 0 } start ? start
                : EasDataMapper.Value(item, EasDataMapper.Body, "InstanceId")) == original);
        if (existing == null)
            return null;
        var result = new XElement(EasDataMapper.Sync + "ApplicationData", existing.Elements().Select(element => new XElement(element)));
        result.Elements().Where(element => element.Name == calendar + "ExceptionStartTime" || element.Name == EasDataMapper.Body + "InstanceId" ||
            element.Name == calendar + "Deleted").Remove();
        return result;
    }

    /// <summary>The series with <paramref name="exception"/> replacing any exception for the same original start.</summary>
    private static XElement WithException(XElement series, XElement exception, string instanceKey)
    {
        var calendar = EasDataMapper.Calendar;
        var result = new XElement(series);
        var exceptions = result.Element(calendar + "Exceptions");
        if (exceptions == null)
        {
            exceptions = new XElement(calendar + "Exceptions");
            result.Add(exceptions);
        }
        var original = EasDataMapper.Date(instanceKey);
        exceptions.Elements(calendar + "Exception").Where(existing =>
            EasDataMapper.Date(EasDataMapper.Value(existing, calendar, "ExceptionStartTime") is { Length: > 0 } start ? start
                : EasDataMapper.Value(existing, EasDataMapper.Body, "InstanceId")) == original).Remove();
        exceptions.Add(exception);
        return result;
    }

    /// <summary>
    /// The calendar and server id of the event whose iCalendar UID is <paramref name="uid"/>, read from the stored
    /// server copies. It reads the database only: opening a message never waits for a running synchronization.
    /// </summary>
    internal async Task<(Guid CalendarId, string RemoteId)?> FindEventByUidAsync(string uid)
    {
        var needle = new XElement(EasDataMapper.Calendar + "UID", uid).ToString(SaveOptions.DisableFormatting);
        needle = needle[needle.IndexOf('>')..];
        var shadow = await _database.Connection.FindWithQueryAsync<EasItemShadow>(
            "SELECT * FROM EasItemShadow WHERE AccountId = ? AND instr(ApplicationData, ?) > 0 AND CollectionId IN (SELECT Id FROM EasCollectionState WHERE AccountId = ? AND Type IN (8, 13))",
            _account.Id, needle, _account.Id).ConfigureAwait(false);
        return shadow == null ? null : (shadow.CollectionId, shadow.RemoteId);
    }

    internal async Task RespondToMeetingAsync(string folderId, string remoteId, string? instanceKey, int responseCode, string? message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            XNamespace meeting = "MeetingResponse:";
            var request = new XElement(meeting + "Request", new XElement(meeting + "UserResponse", responseCode),
                new XElement(meeting + "CollectionId", folderId), new XElement(meeting + "RequestId", remoteId));
            // Unlike Sync, MeetingResponse takes the instance in the extended form; the compact one fails with status 2.
            if (instanceKey != null && EasDataMapper.Date(instanceKey) is { } instance)
                request.Add(new XElement(meeting + "InstanceId", instance.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)));
            // 16.x sends the answer to the organizer only when asked to; 14.x never does (the caller refuses there).
            if (_client.IsAtLeast("16.0"))
                request.Add(new XElement(meeting + "SendResponse", string.IsNullOrWhiteSpace(message) ? null
                    : new XElement(EasDataMapper.Body + "Body", new XElement(EasDataMapper.Body + "Type", "1"), new XElement(EasDataMapper.Body + "Data", message))));
            var response = await _client.CommandAsync("MeetingResponse", new XElement(meeting + "MeetingResponse", request), cancellationToken).ConfigureAwait(false);
            EasClient.RequireSuccess(response?.Element(meeting + "Result") ?? throw new InvalidDataException("The EAS meeting response is missing."));
        }
        finally { _gate.Release(); }
    }
}
