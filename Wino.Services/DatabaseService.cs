using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public interface IDatabaseService : IInitializeAsync
{
    SQLiteAsyncConnection Connection { get; }
}

public class DatabaseService : IDatabaseService
{
    private const string DatabaseName = "Wino200.db";

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
            Connection.CreateTableAsync<MailCategory>(),
            Connection.CreateTableAsync<MailCategoryAssignment>(),
            Connection.CreateTableAsync<MailItemFolder>(),
            Connection.CreateTableAsync<MailAccount>(),
            Connection.CreateTableAsync<AccountContact>(),
            Connection.CreateTableAsync<ContactGroup>(),
            Connection.CreateTableAsync<ContactGroupMember>(),
            Connection.CreateTableAsync<CustomServerInformation>(),
            Connection.CreateTableAsync<AccountSignature>(),
            Connection.CreateTableAsync<EmailTemplate>(),
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
            Connection.CreateTableAsync<MailInvitationCalendarMapping>(),
            Connection.CreateTableAsync<SentMailReceiptState>(),
            Connection.CreateTableAsync<WinoAccount>());

        await EnsureSchemaUpgradesAsync().ConfigureAwait(false);
        await EnsureIndexesAsync().ConfigureAwait(false);
    }

    private async Task EnsureSchemaUpgradesAsync()
    {
        await EnsureKeyboardShortcutSchemaAsync().ConfigureAwait(false);

        var mailCopyColumns = await Connection.GetTableInfoAsync(nameof(MailCopy)).ConfigureAwait(false);

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.IsPinned)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.IsPinned)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        var accountColumns = await Connection.GetTableInfoAsync(nameof(MailAccount)).ConfigureAwait(false);

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.CreatedAt)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.CreatedAt)} TEXT NULL")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.InitialSynchronizationRange)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.InitialSynchronizationRange)} INTEGER NOT NULL DEFAULT {(int)InitialSynchronizationRange.SixMonths}")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsMailAccessGranted)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsMailAccessGranted)} INTEGER NOT NULL DEFAULT 1")
                .ConfigureAwait(false);
        }

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

        if (!folderColumns.Any(c => c.Name == nameof(MailItemFolder.Order)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailItemFolder)} ADD COLUMN \"{nameof(MailItemFolder.Order)}\" INTEGER NOT NULL DEFAULT 0")
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

        var calendarItemColumns = await Connection.GetTableInfoAsync(nameof(CalendarItem)).ConfigureAwait(false);

        if (!calendarItemColumns.Any(c => c.Name == nameof(CalendarItem.SnoozedUntil)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CalendarItem)} ADD COLUMN {nameof(CalendarItem.SnoozedUntil)} TEXT NULL")
                .ConfigureAwait(false);
        }

        var contactColumns = await Connection.GetTableInfoAsync(nameof(AccountContact)).ConfigureAwait(false);

        if (!contactColumns.Any(c => c.Name == nameof(AccountContact.ContactPictureFileId)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(AccountContact)} ADD COLUMN {nameof(AccountContact.ContactPictureFileId)} TEXT NULL")
                .ConfigureAwait(false);
        }

        var accountCalendarColumns = await Connection.GetTableInfoAsync(nameof(AccountCalendar)).ConfigureAwait(false);

        if (!accountCalendarColumns.Any(c => c.Name == nameof(AccountCalendar.IsBackgroundColorUserOverridden)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(AccountCalendar)} ADD COLUMN {nameof(AccountCalendar.IsBackgroundColorUserOverridden)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountCalendarColumns.Any(c => c.Name == nameof(AccountCalendar.IsReadOnly)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(AccountCalendar)} ADD COLUMN {nameof(AccountCalendar.IsReadOnly)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        await Connection.ExecuteAsync("DROP TABLE IF EXISTS WinoAccountAddOnCache").ConfigureAwait(false);
    }

    private async Task EnsureKeyboardShortcutSchemaAsync()
    {
        var keyboardShortcutColumns = await Connection.GetTableInfoAsync(nameof(KeyboardShortcut)).ConfigureAwait(false);

        if (!keyboardShortcutColumns.Any(c => c.Name == nameof(KeyboardShortcut.Mode)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(KeyboardShortcut)} ADD COLUMN {nameof(KeyboardShortcut.Mode)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!keyboardShortcutColumns.Any(c => c.Name == nameof(KeyboardShortcut.Action)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(KeyboardShortcut)} ADD COLUMN {nameof(KeyboardShortcut.Action)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);

            await Connection.ExecuteAsync($@"
UPDATE {nameof(KeyboardShortcut)}
SET {nameof(KeyboardShortcut.Action)} =
    CASE
        WHEN MailOperation = {(int)MailOperation.Archive} THEN {(int)KeyboardShortcutAction.ToggleArchive}
        WHEN MailOperation = {(int)MailOperation.UnArchive} THEN {(int)KeyboardShortcutAction.ToggleArchive}
        WHEN MailOperation = {(int)MailOperation.SoftDelete} THEN {(int)KeyboardShortcutAction.Delete}
        WHEN MailOperation = {(int)MailOperation.HardDelete} THEN {(int)KeyboardShortcutAction.Delete}
        WHEN MailOperation = {(int)MailOperation.Move} THEN {(int)KeyboardShortcutAction.Move}
        WHEN MailOperation = {(int)MailOperation.SetFlag} THEN {(int)KeyboardShortcutAction.ToggleFlag}
        WHEN MailOperation = {(int)MailOperation.ClearFlag} THEN {(int)KeyboardShortcutAction.ToggleFlag}
        WHEN MailOperation = {(int)MailOperation.MarkAsRead} THEN {(int)KeyboardShortcutAction.ToggleReadUnread}
        WHEN MailOperation = {(int)MailOperation.MarkAsUnread} THEN {(int)KeyboardShortcutAction.ToggleReadUnread}
        WHEN MailOperation = {(int)MailOperation.Reply} THEN {(int)KeyboardShortcutAction.Reply}
        WHEN MailOperation = {(int)MailOperation.ReplyAll} THEN {(int)KeyboardShortcutAction.ReplyAll}
        WHEN MailOperation = {(int)MailOperation.Forward} THEN {(int)KeyboardShortcutAction.Reply}
        ELSE {(int)KeyboardShortcutAction.None}
    END").ConfigureAwait(false);
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
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategory_MailAccountId ON MailCategory(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategory_MailAccountId_Name ON MailCategory(MailAccountId, Name)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategory_MailAccountId_IsFavorite ON MailCategory(MailAccountId, IsFavorite)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategoryAssignment_MailCategoryId ON MailCategoryAssignment(MailCategoryId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategoryAssignment_MailCopyUniqueId ON MailCategoryAssignment(MailCopyUniqueId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_MailCategoryAssignment_Category_MailCopy ON MailCategoryAssignment(MailCategoryId, MailCopyUniqueId)").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId ON MailItemFolder(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_RemoteFolderId ON MailItemFolder(MailAccountId, RemoteFolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_ParentRemoteFolderId ON MailItemFolder(MailAccountId, ParentRemoteFolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_SpecialFolderType ON MailItemFolder(MailAccountId, SpecialFolderType)").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccount_MergedInboxId ON MailAccount(MergedInboxId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccount_Order ON MailAccount([Order])").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_AccountSignature_MailAccountId ON AccountSignature(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_EmailTemplate_Name ON EmailTemplate(Name)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccountAlias_AccountId ON MailAccountAlias(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccountAlias_AccountId_AliasAddress ON MailAccountAlias(AccountId, AliasAddress)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_MailAccountPreferences_AccountId ON MailAccountPreferences(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomServerInformation_AccountId ON CustomServerInformation(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_WinoAccount_Email ON WinoAccount(Email)").ConfigureAwait(false);

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
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_SentMailReceiptState_MailUniqueId ON SentMailReceiptState(MailUniqueId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_SentMailReceiptState_AccountId_MessageId ON SentMailReceiptState(AccountId, MessageId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_SentMailReceiptState_Status ON SentMailReceiptState(Status)").ConfigureAwait(false);
    }
}
