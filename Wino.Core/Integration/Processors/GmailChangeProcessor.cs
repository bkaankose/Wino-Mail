using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Apis.Calendar.v3.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

public class GmailChangeProcessor : DefaultChangeProcessor, IGmailChangeProcessor
{
    public GmailChangeProcessor(IDatabaseService databaseService,
                                IFolderService folderService,
                                IMailService mailService,
                                ICalendarService calendarService,
                                IAccountService accountService,
                                ICalendarServiceEx calendarServiceEx,
                                IMimeFileService mimeFileService) : base(databaseService, folderService, mailService, calendarService, accountService, calendarServiceEx, mimeFileService)
    {

    }

    public Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId)
        => MailService.MapLocalDraftAsync(mailCopyId, newDraftId, newThreadId);

    public Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
        => MailService.CreateAssignmentAsync(accountId, mailCopyId, remoteFolderId);

    public async Task ManageCalendarEventAsync(Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount)
    {
        // TODO:
    }

    private string GetOrganizerName(Event calendarEvent, MailAccount account)
    {
        if (calendarEvent.Organizer == null) return string.Empty;

        if (calendarEvent.Organizer.Self == true)
        {
            return account.SenderName;
        }
        else
            return calendarEvent.Organizer.DisplayName;
    }

    private string GetOrganizerEmail(Event calendarEvent, MailAccount account)
    {
        if (calendarEvent.Organizer == null) return string.Empty;

        if (calendarEvent.Organizer.Self == true)
        {
            return account.Address;
        }
        else
            return calendarEvent.Organizer.Email;
    }

    private CalendarItemStatus GetStatus(string status)
    {
        return status switch
        {
            "confirmed" => CalendarItemStatus.Confirmed,
            "tentative" => CalendarItemStatus.Tentative,
            "cancelled" => CalendarItemStatus.Cancelled,
            _ => CalendarItemStatus.Confirmed
        };
    }

    private CalendarItemVisibility GetVisibility(string visibility)
    {
        /// Visibility of the event. Optional. Possible values are:   - "default" - Uses the default visibility for
        /// events on the calendar. This is the default value.  - "public" - The event is public and event details are
        /// visible to all readers of the calendar.  - "private" - The event is private and only event attendees may
        /// view event details.  - "confidential" - The event is private. This value is provided for compatibility
        /// reasons.

        return visibility switch
        {
            "default" => CalendarItemVisibility.Default,
            "public" => CalendarItemVisibility.Public,
            "private" => CalendarItemVisibility.Private,
            "confidential" => CalendarItemVisibility.Confidential,
            _ => CalendarItemVisibility.Default
        };
    }

    public Task<bool> HasAccountAnyDraftAsync(Guid accountId)
        => MailService.HasAccountAnyDraftAsync(accountId);

    public Task<GmailArchiveComparisonResult> GetGmailArchiveComparisonResultAsync(Guid archiveFolderId, List<string> onlineArchiveMailIds)
        => MailService.GetGmailArchiveComparisonResultAsync(archiveFolderId, onlineArchiveMailIds);
}
