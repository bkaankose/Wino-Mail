using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public interface IDatabaseService : IInitializeAsync
{
    SQLiteAsyncConnection Connection { get; }
}

public class DatabaseService : IDatabaseService
{
    private const string DatabaseName = "Wino180.db";

    private bool _isInitialized = false;
    private readonly IApplicationConfiguration _folderConfiguration;

    public SQLiteAsyncConnection Connection { get; private set; }

    public DatabaseService(IApplicationConfiguration folderConfiguration)
    {
        _folderConfiguration = folderConfiguration;
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        var publisherCacheFolder = _folderConfiguration.PublisherSharedFolderPath;
        var databaseFileName = Path.Combine(publisherCacheFolder, DatabaseName);

        Connection = new SQLiteAsyncConnection(databaseFileName);
        await Connection.ExecuteAsync("PRAGMA foreign_keys = ON;").ConfigureAwait(false);

        await CreateTablesAsync();

        _isInitialized = true;
    }

    private async Task CreateTablesAsync()
    {
        await Task.WhenAll(
            Connection.CreateTableAsync<MailCopy>(),
            Connection.CreateTableAsync<MailItemFolder>(),
            Connection.CreateTableAsync<MailAccount>(),
            Connection.CreateTableAsync<AccountContact>(),
            Connection.CreateTableAsync<ContactGroup>(),
            Connection.CreateTableAsync<ContactGroupMember>(),
            Connection.CreateTableAsync<CustomServerInformation>(),
            Connection.CreateTableAsync<AccountSignature>(),
            Connection.CreateTableAsync<MergedInbox>(),
            Connection.CreateTableAsync<MailAccountPreferences>(),
            Connection.CreateTableAsync<MailAccountAlias>(),
            Connection.CreateTableAsync<Thumbnail>(),
            Connection.CreateTableAsync<KeyboardShortcut>(),
            Connection.CreateTableAsync<AccountCalendar>(),
            Connection.CreateTableAsync<CalendarEventAttendee>(),
            Connection.CreateTableAsync<CalendarItem>(),
            Connection.CreateTableAsync<CalendarAttachment>(),
            Connection.CreateTableAsync<Reminder>(),
            Connection.CreateTableAsync<MailInvitationCalendarMapping>()
            );

        await EnsureSchemaUpgradesAsync().ConfigureAwait(false);
        await EnsureIndexesAsync().ConfigureAwait(false);
    }

    private async Task EnsureSchemaUpgradesAsync()
    {
        var folderColumns = await Connection.GetTableInfoAsync(nameof(MailItemFolder)).ConfigureAwait(false);

        if (!folderColumns.Any(c => c.Name == nameof(MailItemFolder.HighestKnownUid)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailItemFolder)} ADD COLUMN {nameof(MailItemFolder.HighestKnownUid)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!folderColumns.Any(c => c.Name == nameof(MailItemFolder.LastUidReconcileUtc)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailItemFolder)} ADD COLUMN {nameof(MailItemFolder.LastUidReconcileUtc)} TEXT NULL")
                .ConfigureAwait(false);
        }

        var customServerColumns = await Connection.GetTableInfoAsync(nameof(CustomServerInformation)).ConfigureAwait(false);

        if (!customServerColumns.Any(c => c.Name == nameof(CustomServerInformation.CalDavServiceUrl)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CustomServerInformation)} ADD COLUMN {nameof(CustomServerInformation.CalDavServiceUrl)} TEXT NULL")
                .ConfigureAwait(false);
        }

        if (!customServerColumns.Any(c => c.Name == nameof(CustomServerInformation.CalDavUsername)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CustomServerInformation)} ADD COLUMN {nameof(CustomServerInformation.CalDavUsername)} TEXT NULL")
                .ConfigureAwait(false);
        }

        if (!customServerColumns.Any(c => c.Name == nameof(CustomServerInformation.CalDavPassword)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CustomServerInformation)} ADD COLUMN {nameof(CustomServerInformation.CalDavPassword)} TEXT NULL")
                .ConfigureAwait(false);
        }

        if (!customServerColumns.Any(c => c.Name == nameof(CustomServerInformation.CalendarSupportMode)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CustomServerInformation)} ADD COLUMN {nameof(CustomServerInformation.CalendarSupportMode)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        var contactColumns = await Connection.GetTableInfoAsync(nameof(AccountContact)).ConfigureAwait(false);

        if (!contactColumns.Any(c => c.Name == nameof(AccountContact.ContactPictureFileId)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(AccountContact)} ADD COLUMN {nameof(AccountContact.ContactPictureFileId)} TEXT NULL")
                .ConfigureAwait(false);
        }
    }

    private async Task EnsureIndexesAsync()
    {
        // Mail indexes
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_Id ON MailCopy(Id)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId ON MailCopy(FolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_Id_FolderId ON MailCopy(Id, FolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_ThreadId ON MailCopy(ThreadId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_MessageId ON MailCopy(MessageId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId_IsRead ON MailCopy(FolderId, IsRead)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_CreationDate ON MailCopy(CreationDate)").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId ON MailItemFolder(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_RemoteFolderId ON MailItemFolder(MailAccountId, RemoteFolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_ParentRemoteFolderId ON MailItemFolder(MailAccountId, ParentRemoteFolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_SpecialFolderType ON MailItemFolder(MailAccountId, SpecialFolderType)").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccount_MergedInboxId ON MailAccount(MergedInboxId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccount_Order ON MailAccount([Order])").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_AccountSignature_MailAccountId ON AccountSignature(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccountAlias_AccountId ON MailAccountAlias(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccountAlias_AccountId_AliasAddress ON MailAccountAlias(AccountId, AliasAddress)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_MailAccountPreferences_AccountId ON MailAccountPreferences(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomServerInformation_AccountId ON CustomServerInformation(AccountId)").ConfigureAwait(false);

        // Calendar indexes
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_AccountCalendar_AccountId ON AccountCalendar(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_CalendarItem_CalendarId ON CalendarItem(CalendarId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_CalendarItem_CalendarId_RemoteEventId ON CalendarItem(CalendarId, RemoteEventId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_CalendarItem_RecurringCalendarItemId ON CalendarItem(RecurringCalendarItemId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_CalendarEventAttendee_CalendarItemId ON CalendarEventAttendee(CalendarItemId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_Reminder_CalendarItemId ON Reminder(CalendarItemId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_CalendarAttachment_CalendarItemId ON CalendarAttachment(CalendarItemId)").ConfigureAwait(false);

        // Invitation mapping indexes
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailInvitationCalendarMapping_AccountId ON MailInvitationCalendarMapping(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailInvitationCalendarMapping_MailCopyId ON MailInvitationCalendarMapping(MailCopyId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailInvitationCalendarMapping_InvitationUid ON MailInvitationCalendarMapping(InvitationUid)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailInvitationCalendarMapping_CalendarId ON MailInvitationCalendarMapping(CalendarId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailInvitationCalendarMapping_CalendarItemId ON MailInvitationCalendarMapping(CalendarItemId)").ConfigureAwait(false);
    }
}
