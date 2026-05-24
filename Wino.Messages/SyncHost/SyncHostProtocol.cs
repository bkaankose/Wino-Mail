namespace Wino.Messaging.SyncHost;

public static class SyncHostProtocol
{
    public const int Version = 1;
    public const string CommandPipeName = "WinoMail.SyncHost.Commands";
    public const string EventPipeName = "WinoMail.SyncHost.Events";
    public const string RunningMutexName = "Local\\WinoMailSyncHostRunning";

    public static class Commands
    {
        public const string SynchronizeMail = nameof(SynchronizeMail);
        public const string SynchronizeCalendar = nameof(SynchronizeCalendar);
        public const string QueueRequests = nameof(QueueRequests);
        public const string IsAccountSynchronizing = nameof(IsAccountSynchronizing);
        public const string GetSynchronizationProgress = nameof(GetSynchronizationProgress);
        public const string DownloadMimeMessage = nameof(DownloadMimeMessage);
        public const string DownloadCalendarAttachment = nameof(DownloadCalendarAttachment);
        public const string CancelSynchronizations = nameof(CancelSynchronizations);
        public const string DestroySynchronizer = nameof(DestroySynchronizer);
        public const string GetPendingMailOperationIds = nameof(GetPendingMailOperationIds);
        public const string GetPendingCalendarOperationIds = nameof(GetPendingCalendarOperationIds);
        public const string OnlineSearch = nameof(OnlineSearch);
        public const string ShutdownHost = nameof(ShutdownHost);
    }

    public static class Events
    {
        public const string AccountSynchronizationProgressUpdated = nameof(AccountSynchronizationProgressUpdated);
        public const string AccountSynchronizationCompleted = nameof(AccountSynchronizationCompleted);
        public const string AccountSynchronizerStateChanged = nameof(AccountSynchronizerStateChanged);
        public const string SynchronizationActionsAdded = nameof(SynchronizationActionsAdded);
        public const string SynchronizationActionsCompleted = nameof(SynchronizationActionsCompleted);
        public const string RefreshUnreadCounts = nameof(RefreshUnreadCounts);
        public const string AccountFolderConfigurationUpdated = nameof(AccountFolderConfigurationUpdated);
        public const string AccountCacheReset = nameof(AccountCacheReset);
        public const string MailAdded = nameof(MailAdded);
        public const string BulkMailAdded = nameof(BulkMailAdded);
        public const string MailRemoved = nameof(MailRemoved);
        public const string BulkMailRemoved = nameof(BulkMailRemoved);
        public const string MailUpdated = nameof(MailUpdated);
        public const string BulkMailUpdated = nameof(BulkMailUpdated);
        public const string MailStateUpdated = nameof(MailStateUpdated);
        public const string BulkMailStateUpdated = nameof(BulkMailStateUpdated);
        public const string MailDownloaded = nameof(MailDownloaded);
        public const string DraftCreated = nameof(DraftCreated);
        public const string DraftFailed = nameof(DraftFailed);
        public const string DraftMapped = nameof(DraftMapped);
        public const string FolderRenamed = nameof(FolderRenamed);
        public const string FolderDeleted = nameof(FolderDeleted);
        public const string FolderSynchronizationEnabled = nameof(FolderSynchronizationEnabled);
        public const string CalendarItemAdded = nameof(CalendarItemAdded);
        public const string CalendarItemUpdated = nameof(CalendarItemUpdated);
        public const string CalendarItemDeleted = nameof(CalendarItemDeleted);
        public const string CalendarListAdded = nameof(CalendarListAdded);
        public const string CalendarListUpdated = nameof(CalendarListUpdated);
        public const string CalendarListDeleted = nameof(CalendarListDeleted);
        public const string AccountCreated = nameof(AccountCreated);
        public const string AccountUpdated = nameof(AccountUpdated);
        public const string AccountRemoved = nameof(AccountRemoved);
        public const string HostSnapshot = nameof(HostSnapshot);
    }
}
