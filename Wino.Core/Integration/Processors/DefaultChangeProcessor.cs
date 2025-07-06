using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Apis.Calendar.v3.Data;
using MimeKit;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

/// <summary>
/// Database change processor that handles common operations for all synchronizers.
/// When a synchronizer detects a change, it should call the appropriate method in this class to reflect the change in the database.
/// Different synchronizers might need additional implementations.
/// <see cref="IGmailChangeProcessor"/>,  <see cref="IOutlookChangeProcessor"/> and  <see cref="IImapChangeProcessor"/>
/// None of the synchronizers can directly change anything in the database.
/// </summary>
public interface IDefaultChangeProcessor : ICalendarServiceEx
{
    Task UpdateAccountAsync(MailAccount account);
    // Task<string> UpdateAccountDeltaSynchronizationIdentifierAsync(Guid accountId, string deltaSynchronizationIdentifier);
    Task DeleteAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);
    Task ChangeMailReadStatusAsync(string mailCopyId, bool isRead);
    Task ChangeFlagStatusAsync(string mailCopyId, bool isFlagged);
    Task<bool> CreateMailAsync(Guid AccountId, NewMailItemPackage package);
    Task DeleteMailAsync(Guid accountId, string mailId);
    Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds);
    Task SaveMimeFileAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId);
    Task DeleteFolderAsync(Guid accountId, string remoteFolderId);
    Task InsertFolderAsync(MailItemFolder folder);
    Task UpdateFolderAsync(MailItemFolder folder);
    Task<List<MailItemFolder>> GetLocalFoldersAsync(Guid accountId);
    Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(MailSynchronizationOptions options);
    Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId);
    Task UpdateFolderLastSyncDateAsync(Guid folderId);
    Task UpdateRemoteAliasInformationAsync(MailAccount account, List<RemoteAccountAlias> remoteAccountAliases);
    // Calendar
    Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId);

    Task DeleteCalendarItemAsync(Guid calendarItemId);

    Task DeleteAccountCalendarAsync(AccountCalendar accountCalendar);
    Task InsertAccountCalendarAsync(AccountCalendar accountCalendar);
    Task UpdateAccountCalendarAsync(AccountCalendar accountCalendar);

    Task UpdateCalendarDeltaSynchronizationToken(Guid calendarId, string deltaToken);
    Task<List<MailCopy>> GetMailCopiesAsync(IEnumerable<string> mailCopyIds);
    Task CreateMailRawAsync(MailAccount account, MailItemFolder mailItemFolder, NewMailItemPackage package);
    Task DeleteUserMailCacheAsync(Guid accountId);

    /// <summary>
    /// Checks whether the mail exists in the folder.
    /// When deciding Create or Update existing mail, we need to check if the mail exists in the folder.
    /// Also duplicate assignments for Gmail's virtual Archive folder is ignored.
    /// </summary>
    /// <param name="messageId">Message id</param>
    /// <param name="folderId">Folder's local id.</param>
    /// <returns>Whether mail exists in the folder or not.</returns>
    Task<bool> IsMailExistsInFolderAsync(string messageId, Guid folderId);
    Task<List<string>> AreMailsExistsAsync(IEnumerable<string> mailCopyIds);
    Task<string> UpdateAccountDeltaSynchronizationIdentifierAsync(Guid accountId, string synchronizationDeltaIdentifier);
}

public interface IGmailChangeProcessor : IDefaultChangeProcessor
{
    Task<bool> HasAccountAnyDraftAsync(Guid accountId);
    Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId);
    Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);
    Task ManageCalendarEventAsync(Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount);
    Task<GmailArchiveComparisonResult> GetGmailArchiveComparisonResultAsync(Guid archiveFolderId, List<string> onlineArchiveMailIds);
}

public interface IOutlookChangeProcessor : IDefaultChangeProcessor
{
    /// <summary>
    /// Updates Folder's delta synchronization identifier.
    /// Only used in Outlook since it does per-folder sync.
    /// </summary>
    /// <param name="folderId">Folder id</param>
    /// <param name="synchronizationIdentifier">New synchronization identifier.</param>
    /// <returns>New identifier if success.</returns>
    Task UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string deltaSynchronizationIdentifier);

    /// <summary>
    /// Outlook may expire folder's delta token after a while.
    /// Recommended action for this scenario is to reset token and do full sync.
    /// This method resets the token for the given folder.
    /// </summary>
    /// <param name="folderId">Local folder id to reset token for.</param>
    /// <returns>Empty string to assign folder delta sync for.</returns>
    Task<string> ResetFolderDeltaTokenAsync(Guid folderId);

    Task ManageCalendarEventAsync(Microsoft.Graph.Models.Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount);
}

public interface IImapChangeProcessor : IDefaultChangeProcessor
{
    /// <summary>
    /// Returns all known uids for the given folder.
    /// </summary>
    /// <param name="folderId">Folder id to retrieve uIds for.</param>
    Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId);
}

public class DefaultChangeProcessor(IDatabaseService databaseService,
                              IFolderService folderService,
                              IMailService mailService,
                              ICalendarService calendarService,
                              IAccountService accountService,
                              ICalendarServiceEx calendarServiceEx,
                              IMimeFileService mimeFileService) : BaseDatabaseService(databaseService), IDefaultChangeProcessor, ICalendarServiceEx
{
    protected IMailService MailService = mailService;
    protected ICalendarService CalendarService = calendarService;
    protected IFolderService FolderService = folderService;
    protected IAccountService AccountService = accountService;
    private readonly ICalendarServiceEx _calendarServiceEx = calendarServiceEx;
    private readonly IMimeFileService _mimeFileService = mimeFileService;

    public Task<string> UpdateAccountDeltaSynchronizationIdentifierAsync(Guid accountId, string synchronizationDeltaIdentifier)
        => AccountService.UpdateSyncIdentifierRawAsync(accountId, synchronizationDeltaIdentifier);

    public Task ChangeFlagStatusAsync(string mailCopyId, bool isFlagged)
        => MailService.ChangeFlagStatusAsync(mailCopyId, isFlagged);

    public Task<List<string>> AreMailsExistsAsync(IEnumerable<string> mailCopyIds)
        => MailService.AreMailsExistsAsync(mailCopyIds);

    public Task<List<MailCopy>> GetMailCopiesAsync(IEnumerable<string> mailCopyIds)
        => MailService.GetMailItemsAsync(mailCopyIds);

    public Task ChangeMailReadStatusAsync(string mailCopyId, bool isRead)
        => MailService.ChangeReadStatusAsync(mailCopyId, isRead);

    public Task DeleteAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
        => MailService.DeleteAssignmentAsync(accountId, mailCopyId, remoteFolderId);

    public Task DeleteMailAsync(Guid accountId, string mailId)
        => MailService.DeleteMailAsync(accountId, mailId);

    public Task<bool> CreateMailAsync(Guid accountId, NewMailItemPackage package)
        => MailService.CreateMailAsync(accountId, package);

    public Task CreateMailRawAsync(MailAccount account, MailItemFolder mailItemFolder, NewMailItemPackage package)
        => MailService.CreateMailRawAsync(account, mailItemFolder, package);

    public Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId)
        => MailService.MapLocalDraftAsync(accountId, localDraftCopyUniqueId, newMailCopyId, newDraftId, newThreadId);

    public Task<List<MailItemFolder>> GetLocalFoldersAsync(Guid accountId)
        => FolderService.GetFoldersAsync(accountId);

    public Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(MailSynchronizationOptions options)
        => FolderService.GetSynchronizationFoldersAsync(options);

    public Task DeleteFolderAsync(Guid accountId, string remoteFolderId)
        => FolderService.DeleteFolderAsync(accountId, remoteFolderId);

    public Task InsertFolderAsync(MailItemFolder folder)
        => FolderService.InsertFolderAsync(folder);

    public Task UpdateFolderAsync(MailItemFolder folder)
        => FolderService.UpdateFolderAsync(folder);

    public Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds)
        => MailService.GetDownloadedUnreadMailsAsync(accountId, downloadedMailCopyIds);

    public Task SaveMimeFileAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId)
        => _mimeFileService.SaveMimeMessageAsync(fileId, mimeMessage, accountId);

    public Task UpdateFolderLastSyncDateAsync(Guid folderId)
        => FolderService.UpdateFolderLastSyncDateAsync(folderId);

    public Task UpdateAccountAsync(MailAccount account)
        => AccountService.UpdateAccountAsync(account);

    public Task UpdateRemoteAliasInformationAsync(MailAccount account, List<RemoteAccountAlias> remoteAccountAliases)
        => AccountService.UpdateRemoteAliasInformationAsync(account, remoteAccountAliases);

    public Task<List<AccountCalendar>> GetAccountCalendarsAsync(Guid accountId)
        => CalendarService.GetAccountCalendarsAsync(accountId);

    public Task DeleteCalendarItemAsync(Guid calendarItemId)
        => CalendarService.DeleteCalendarItemAsync(calendarItemId);

    public Task DeleteAccountCalendarAsync(AccountCalendar accountCalendar)
        => CalendarService.DeleteAccountCalendarAsync(accountCalendar);

    public Task InsertAccountCalendarAsync(AccountCalendar accountCalendar)
        => CalendarService.InsertAccountCalendarAsync(accountCalendar);

    public Task UpdateAccountCalendarAsync(AccountCalendar accountCalendar)
        => CalendarService.UpdateAccountCalendarAsync(accountCalendar);

    public Task UpdateCalendarDeltaSynchronizationToken(Guid calendarId, string deltaToken)
        => CalendarService.UpdateCalendarDeltaSynchronizationToken(calendarId, deltaToken);

    public async Task DeleteUserMailCacheAsync(Guid accountId)
    {
        await _mimeFileService.DeleteUserMimeCacheAsync(accountId).ConfigureAwait(false);
        await AccountService.DeleteAccountMailCacheAsync(accountId, AccountCacheResetReason.ExpiredCache).ConfigureAwait(false);
    }

    public Task<bool> IsMailExistsInFolderAsync(string messageId, Guid folderId)
            => MailService.IsMailExistsAsync(messageId, folderId);

    // TODO: Normalize this shit. Not everything needs to be exposed for processor.
    #region ICalendarServiceEx

    public Task<int> ClearAllCalendarEventAttendeesAsync()
    {
        return _calendarServiceEx.ClearAllCalendarEventAttendeesAsync();
    }

    public Task<int> ClearAllCalendarsAsync()
    {
        return _calendarServiceEx.ClearAllCalendarsAsync();
    }

    public Task<int> ClearAllDataAsync()
    {
        return _calendarServiceEx.ClearAllDataAsync();
    }

    public Task<int> ClearAllEventsAsync()
    {
        return _calendarServiceEx.ClearAllEventsAsync();
    }

    public Task<int> DeleteCalendarAsync(string remoteCalendarId)
    {
        return _calendarServiceEx.DeleteCalendarAsync(remoteCalendarId);
    }

    public Task<int> DeleteCalendarEventAttendeesForEventAsync(Guid eventId)
    {
        return _calendarServiceEx.DeleteCalendarEventAttendeesForEventAsync(eventId);
    }

    public Task<int> DeleteEventAsync(string remoteEventId)
    {
        return _calendarServiceEx.DeleteEventAsync(remoteEventId);
    }

    public Task<List<CalendarEventAttendee>> GetAllCalendarEventAttendeesAsync()
    {
        return _calendarServiceEx.GetAllCalendarEventAttendeesAsync();
    }

    public Task<List<AccountCalendar>> GetAllCalendarsAsync()
    {
        return _calendarServiceEx.GetAllCalendarsAsync();
    }

    public Task<List<CalendarItem>> GetAllDayEventsAsync()
    {
        return _calendarServiceEx.GetAllDayEventsAsync();
    }

    public Task<List<CalendarItem>> GetAllEventsAsync()
    {
        return _calendarServiceEx.GetAllEventsAsync();
    }

    public Task<List<CalendarItem>> GetAllEventsIncludingDeletedAsync()
    {
        return _calendarServiceEx.GetAllEventsIncludingDeletedAsync();
    }

    public Task<List<CalendarItem>> GetAllRecurringEventsByTypeAsync()
    {
        return _calendarServiceEx.GetAllRecurringEventsByTypeAsync();
    }

    public Task<AccountCalendar> GetCalendarByRemoteIdAsync(string remoteCalendarId)
    {
        return _calendarServiceEx.GetCalendarByRemoteIdAsync(remoteCalendarId);
    }

    public Task<Dictionary<AttendeeResponseStatus, int>> GetCalendarEventAttendeeResponseCountsAsync(Guid eventId)
    {
        return _calendarServiceEx.GetCalendarEventAttendeeResponseCountsAsync(eventId);
    }

    public Task<List<CalendarEventAttendee>> GetCalendarEventAttendeesForEventAsync(Guid eventId)
    {
        return _calendarServiceEx.GetCalendarEventAttendeesForEventAsync(eventId);
    }

    public Task<List<CalendarEventAttendee>> GetCalendarEventAttendeesForEventByRemoteIdAsync(string remoteEventId)
    {
        return _calendarServiceEx.GetCalendarEventAttendeesForEventByRemoteIdAsync(remoteEventId);
    }

    public Task<string> GetCalendarSyncTokenAsync(string calendarId)
    {
        return _calendarServiceEx.GetCalendarSyncTokenAsync(calendarId);
    }

    public Task<CalendarItem> GetEventByRemoteIdAsync(string remoteEventId)
    {
        return _calendarServiceEx.GetEventByRemoteIdAsync(remoteEventId);
    }

    public Task<List<CalendarItem>> GetEventsByItemTypeAsync(CalendarItemType itemType)
    {
        return _calendarServiceEx.GetEventsByItemTypeAsync(itemType);
    }

    public Task<List<CalendarItem>> GetEventsByItemTypesAsync(params CalendarItemType[] itemTypes)
    {
        return _calendarServiceEx.GetEventsByItemTypesAsync(itemTypes);
    }

    public Task<List<CalendarItem>> GetEventsByremoteCalendarIdAsync(string remoteCalendarId)
    {
        return _calendarServiceEx.GetEventsByremoteCalendarIdAsync(remoteCalendarId);
    }

    public Task<List<CalendarItem>> GetEventsForCalendarAsync(Guid calendarId)
    {
        return _calendarServiceEx.GetEventsForCalendarAsync(calendarId);
    }

    public Task<List<CalendarItem>> GetEventsInDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        return _calendarServiceEx.GetEventsInDateRangeAsync(startDate, endDate);
    }

    public Task<List<CalendarItem>> GetEventsSinceLastSyncAsync(DateTime? lastSyncTime)
    {
        return _calendarServiceEx.GetEventsSinceLastSyncAsync(lastSyncTime);
    }

    public Task<Dictionary<CalendarItemType, int>> GetEventStatsByItemTypeAsync()
    {
        return _calendarServiceEx.GetEventStatsByItemTypeAsync();
    }

    public Task<List<CalendarItem>> GetExpandedEventsInDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        return _calendarServiceEx.GetExpandedEventsInDateRangeAsync(startDate, endDate);
    }

    public Task<List<CalendarItem>> GetExpandedEventsInDateRangeWithExceptionsAsync(DateTime startDate, DateTime endDate, AccountCalendar calendar)
    {
        return _calendarServiceEx.GetExpandedEventsInDateRangeWithExceptionsAsync(startDate, endDate, calendar);
    }

    public Task<DateTime?> GetLastSyncTimeAsync(string calendarId)
    {
        return _calendarServiceEx.GetLastSyncTimeAsync(calendarId);
    }

    public Task<List<CalendarItem>> GetMultiDayEventsAsync()
    {
        return _calendarServiceEx.GetMultiDayEventsAsync();
    }

    public Task<List<CalendarItem>> GetRecurringEventsAsync()
    {
        return _calendarServiceEx.GetRecurringEventsAsync();
    }

    public Task<int> HardDeleteEventAsync(string remoteEventId)
    {
        return _calendarServiceEx.HardDeleteEventAsync(remoteEventId);
    }

    public Task<int> InsertCalendarAsync(AccountCalendar calendar)
    {
        return _calendarServiceEx.InsertCalendarAsync(calendar);
    }

    public Task<int> InsertCalendarEventAttendeeAsync(CalendarEventAttendee calendareventattendee)
    {
        return _calendarServiceEx.InsertCalendarEventAttendeeAsync(calendareventattendee);
    }

    public Task<int> InsertEventAsync(CalendarItem calendarItem)
    {
        return _calendarServiceEx.InsertEventAsync(calendarItem);
    }

    public Task<int> MarkEventAsDeletedAsync(string remoteEventId, string remoteCalendarId)
    {
        return _calendarServiceEx.MarkEventAsDeletedAsync(remoteEventId, remoteCalendarId);
    }

    public Task<int> SyncCalendarEventAttendeesForEventAsync(Guid eventId, List<CalendarEventAttendee> calendareventattendees)
    {
        return _calendarServiceEx.SyncCalendarEventAttendeesForEventAsync(eventId, calendareventattendees);
    }

    public Task<int> UpdateAllEventItemTypesAsync()
    {
        return _calendarServiceEx.UpdateAllEventItemTypesAsync();
    }

    public Task<int> UpdateCalendarAsync(AccountCalendar calendar)
    {
        return _calendarServiceEx.UpdateCalendarAsync(calendar);
    }

    public Task<int> UpdateCalendarEventAttendeeAsync(CalendarEventAttendee calendareventattendee)
    {
        return _calendarServiceEx.UpdateCalendarEventAttendeeAsync(calendareventattendee);
    }

    public Task<int> UpdateCalendarSyncTokenAsync(string calendarId, string syncToken)
    {
        return _calendarServiceEx.UpdateCalendarSyncTokenAsync(calendarId, syncToken);
    }

    public Task<int> UpdateEventAsync(CalendarItem calendarItem)
    {
        return _calendarServiceEx.UpdateEventAsync(calendarItem);
    }

    public Task<int> UpsertCalendarAsync(AccountCalendar calendar)
    {
        return _calendarServiceEx.UpsertCalendarAsync(calendar);
    }

    public Task<int> UpsertEventAsync(CalendarItem calendarItem)
    {
        return _calendarServiceEx.UpsertEventAsync(calendarItem);
    }

    public Task<int> SyncAttendeesForEventAsync(Guid eventId, List<CalendarEventAttendee> attendees)
    {
        return _calendarServiceEx.SyncAttendeesForEventAsync(eventId, attendees);
    }

    #endregion
}
