using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Integration.Processors;
using Wino.Providers.Gmail.Google.Apis.Calendar.v3.Data;

namespace Wino.Providers.Gmail.Integration.Processors;

public interface IGmailChangeProcessor : IDefaultChangeProcessor
{
    Task<bool> HasAccountAnyDraftAsync(Guid accountId);
    Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId);
    Task<bool> RefreshMappedDraftMetadataAsync(Guid accountId, Guid uniqueId, MailCopy remoteCopy);
    Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);
    Task ManageCalendarEventAsync(Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount);
    Task<GmailArchiveComparisonResult> GetGmailArchiveComparisonResultAsync(Guid archiveFolderId, List<string> onlineArchiveMailIds);
}
