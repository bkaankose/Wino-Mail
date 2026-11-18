using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Calendar;
using Wino.Core.Synchronizers.CalDav;

namespace Wino.Core.Services;

/// <summary>
/// Calendar requests of a calendar that lives on this device only. Any account can have one: IMAP and POP3 accounts
/// without CalDAV, and provider accounts while calendar access is wanted but not granted.
/// </summary>
public sealed class LocalCalendarOperationHandler
{
    private readonly MailAccount _account;
    private readonly ICalDavChangeProcessor _changeProcessor;
    private readonly ICalendarService _calendarService;
    private readonly string _applicationDataFolderPath;
    private readonly string _resourceScheme;

    public LocalCalendarOperationHandler(MailAccount account, ICalDavChangeProcessor changeProcessor, ICalendarService calendarService, string applicationDataFolderPath, string resourceScheme)
    {
        _account = account;
        _changeProcessor = changeProcessor;
        _calendarService = calendarService;
        _applicationDataFolderPath = applicationDataFolderPath;
        _resourceScheme = resourceScheme;
    }

    public async Task CreateCalendarEventAsync(CreateCalendarEventRequest request)
    {
        var item = request.PreparedItem;
        var attendees = request.PreparedEvent.Attendees;
        var reminders = request.PreparedEvent.Reminders;
        CalendarIcsBuilder.EnsureCalendarItemDefaults(item, _account, "local");
        item.AssignedCalendar ??= await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);

        var existing = await _calendarService.GetCalendarItemAsync(item.Id).ConfigureAwait(false);

        if (existing == null)
            await _calendarService.CreateNewCalendarItemAsync(item, attendees).ConfigureAwait(false);
        else
            await _calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);

        await _calendarService.SaveRemindersAsync(item.Id, reminders).ConfigureAwait(false);
        await SaveAttachmentsAsync(request.ComposeResult, item.Id).ConfigureAwait(false);
        await PersistIcsAsync(item, attendees).ConfigureAwait(false);
    }

    public async Task UpdateCalendarEventAsync(UpdateCalendarEventRequest request)
    {
        var item = request.Item;
        CalendarIcsBuilder.EnsureCalendarItemDefaults(item, _account, "local");
        item.AssignedCalendar ??= await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);

        var attendees = request.Attendees ?? await _calendarService.GetAttendeesAsync(item.Id).ConfigureAwait(false);

        await _calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
        await PersistIcsAsync(item, attendees).ConfigureAwait(false);
    }

    public async Task DeleteCalendarEventAsync(DeleteCalendarEventRequest request)
    {
        if (request.Item.IsRecurringParent)
        {
            var occurrences = await _calendarService.GetRecurringChildrenAsync(request.Item.Id).ConfigureAwait(false);
            foreach (var occurrence in occurrences ?? [])
                await _changeProcessor.DeleteCalendarItemAsync(occurrence.Id).ConfigureAwait(false);
        }

        await _changeProcessor.DeleteCalendarItemAsync(request.Item.Id).ConfigureAwait(false);
    }

    public async Task AcceptEventAsync(AcceptEventRequest request)
    {
        request.Item.Status = CalendarItemStatus.Accepted;
        await UpdateStatusAsync(request.Item).ConfigureAwait(false);
    }

    public async Task DeclineEventAsync(DeclineEventRequest request)
    {
        request.Item.Status = CalendarItemStatus.Cancelled;
        await UpdateStatusAsync(request.Item).ConfigureAwait(false);
    }

    public async Task TentativeEventAsync(TentativeEventRequest request)
    {
        request.Item.Status = CalendarItemStatus.Tentative;
        await UpdateStatusAsync(request.Item).ConfigureAwait(false);
    }

    private async Task UpdateStatusAsync(CalendarItem item)
    {
        CalendarIcsBuilder.EnsureCalendarItemDefaults(item, _account, "local");
        item.AssignedCalendar ??= await _calendarService.GetAccountCalendarAsync(item.CalendarId).ConfigureAwait(false);

        var attendees = await _calendarService.GetAttendeesAsync(item.Id).ConfigureAwait(false);
        await _calendarService.UpdateCalendarItemAsync(item, attendees).ConfigureAwait(false);
        await PersistIcsAsync(item, attendees).ConfigureAwait(false);
    }

    private Task PersistIcsAsync(CalendarItem item, List<CalendarEventAttendee> attendees)
    {
        var resourceHref = $"{_resourceScheme}://calendar/{item.CalendarId:N}/{item.Id:N}";
        var icsContent = CalendarIcsBuilder.BuildIcsContent(item, attendees);

        return _changeProcessor.SaveCalendarItemIcsAsync(
            _account.Id,
            item.CalendarId,
            item.Id,
            item.RemoteEventId,
            resourceHref,
            DateTimeOffset.UtcNow.ToString("O"),
            icsContent);
    }

    /// <summary>A local attachment is the copy made when the event was saved; opening it copies that file.</summary>
    public static async Task CopyStoredAttachmentAsync(CalendarAttachment attachment, string localFilePath, ICalendarService calendarService)
    {
        if (string.IsNullOrWhiteSpace(attachment.LocalFilePath) || !File.Exists(attachment.LocalFilePath))
            throw new FileNotFoundException("The local calendar attachment file is missing.", attachment.FileName);

        if (!string.Equals(Path.GetFullPath(attachment.LocalFilePath), Path.GetFullPath(localFilePath), StringComparison.Ordinal))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(localFilePath));
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.Copy(attachment.LocalFilePath, localFilePath, overwrite: true);
        }

        await calendarService.MarkAttachmentDownloadedAsync(attachment.Id, localFilePath).ConfigureAwait(false);
    }

    private async Task SaveAttachmentsAsync(CalendarEventComposeResult composeResult, Guid calendarItemId)
    {
        await _calendarService.DeleteAttachmentsAsync(calendarItemId).ConfigureAwait(false);

        var attachments = composeResult?.Attachments;
        if (attachments == null || attachments.Count == 0)
            return;

        var attachmentsRoot = Path.Combine(_applicationDataFolderPath, "CalendarAttachments", calendarItemId.ToString("N"));
        Directory.CreateDirectory(attachmentsRoot);

        var storedAttachments = new List<CalendarAttachment>();

        foreach (var attachment in attachments.Where(a => !string.IsNullOrWhiteSpace(a.FilePath) && File.Exists(a.FilePath)))
        {
            var fileName = string.IsNullOrWhiteSpace(attachment.FileName) ? Path.GetFileName(attachment.FilePath) : attachment.FileName;
            var destinationPath = Path.Combine(attachmentsRoot, fileName);
            File.Copy(attachment.FilePath, destinationPath, overwrite: true);

            storedAttachments.Add(new CalendarAttachment
            {
                Id = Guid.NewGuid(),
                CalendarItemId = calendarItemId,
                RemoteAttachmentId = attachment.Id.ToString("N"),
                FileName = fileName,
                Size = attachment.Size,
                ContentType = MimeTypes.GetMimeType(fileName),
                IsDownloaded = true,
                LocalFilePath = destinationPath,
                LastModified = DateTimeOffset.UtcNow
            });
        }

        if (storedAttachments.Count > 0)
        {
            await _calendarService.InsertOrReplaceAttachmentsAsync(storedAttachments).ConfigureAwait(false);
        }
    }
}
