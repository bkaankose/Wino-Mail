using System;
using System.Collections.Generic;
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

        await MigrateLegacyContactsAsync().ConfigureAwait(false);
        await CreateTablesAsync();

        _isInitialized = true;
    }

    private async Task CreateTablesAsync()
    {
        await Task.WhenAll(
            Connection.CreateTableAsync<MailCopy>(),
            Connection.CreateTableAsync<MailCategory>(),
            Connection.CreateTableAsync<MailCategoryAssignment>(),
            Connection.CreateTableAsync<MailFilter>(),
            Connection.CreateTableAsync<MailFilterCondition>(),
            Connection.CreateTableAsync<MailFilterAction>(),
            Connection.CreateTableAsync<MailFilterExecution>(),
            Connection.CreateTableAsync<AccountProviderFeature>(),
            Connection.CreateTableAsync<MailItemFolder>(),
            Connection.CreateTableAsync<FolderConfigurationOverride>(),
            Connection.CreateTableAsync<MailAccount>(),
            Connection.CreateTableAsync<AccountContact>(),
            Connection.CreateTableAsync<ContactAddressBook>(),
            Connection.CreateTableAsync<ContactEmailAddress>(),
            Connection.CreateTableAsync<ContactPhoneNumber>(),
            Connection.CreateTableAsync<ContactPostalAddress>(),
            Connection.CreateTableAsync<ContactImAddress>(),
            Connection.CreateTableAsync<ContactRelation>(),
            Connection.CreateTableAsync<ContactList>(),
            Connection.CreateTableAsync<ContactListMember>(),
            Connection.CreateTableAsync<AccountTaskList>(),
            Connection.CreateTableAsync<AccountTask>(),
            Connection.CreateTableAsync<AccountTaskStep>(),
            Connection.CreateTableAsync<CustomServerInformation>(),
            Connection.CreateTableAsync<MailServerCertificateTrust>(),
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
        await EnsureLocalAddressBooksAsync().ConfigureAwait(false);
        await EnsureLocalTaskListsAsync().ConfigureAwait(false);
    }

    private async Task EnsureSchemaUpgradesAsync()
    {
        await EnsureKeyboardShortcutSchemaAsync().ConfigureAwait(false);
        await EnsureWinoAccountSchemaAsync().ConfigureAwait(false);

        var mailCopyColumns = await Connection.GetTableInfoAsync(nameof(MailCopy)).ConfigureAwait(false);

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.IsPinned)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.IsPinned)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.DraftSyncState)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.DraftSyncState)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.DraftSyncAttemptCount)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.DraftSyncAttemptCount)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.LastDraftSyncAttemptUtc)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.LastDraftSyncAttemptUtc)} TEXT NULL")
                .ConfigureAwait(false);
        }

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.LastDraftSyncError)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.LastDraftSyncError)} TEXT NULL")
                .ConfigureAwait(false);
        }

        await Connection.ExecuteAsync($@"
UPDATE {nameof(MailCopy)}
SET {nameof(MailCopy.DraftSyncState)} = {(int)DraftSyncState.PendingSync}
WHERE {nameof(MailCopy.IsDraft)} = 1
  AND COALESCE({nameof(MailCopy.DraftSyncState)}, {(int)DraftSyncState.None}) = {(int)DraftSyncState.None}
  AND {nameof(MailCopy.DraftId)} LIKE 'localDraft\_%' ESCAPE '\'").ConfigureAwait(false);

        await Connection.ExecuteAsync($@"
UPDATE {nameof(MailCopy)}
SET {nameof(MailCopy.DraftSyncState)} = {(int)DraftSyncState.Synced}
WHERE {nameof(MailCopy.IsDraft)} = 1
  AND COALESCE({nameof(MailCopy.DraftSyncState)}, {(int)DraftSyncState.None}) = {(int)DraftSyncState.None}
  AND {nameof(MailCopy.DraftId)} IS NOT NULL
  AND {nameof(MailCopy.DraftId)} NOT LIKE 'localDraft\_%' ESCAPE '\'").ConfigureAwait(false);

        await Connection.ExecuteAsync($@"
UPDATE {nameof(MailCopy)}
SET {nameof(MailCopy.DraftSyncAttemptCount)} = 0
WHERE {nameof(MailCopy.DraftSyncAttemptCount)} IS NULL").ConfigureAwait(false);

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.ImapUid)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.ImapUid)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);

            await Connection.ExecuteAsync($@"
UPDATE {nameof(MailCopy)}
SET {nameof(MailCopy.ImapUid)} = CAST(SUBSTR({nameof(MailCopy.Id)}, INSTR({nameof(MailCopy.Id)}, '_') + 1) AS INTEGER)
WHERE {nameof(MailCopy.Id)} IS NOT NULL
  AND INSTR({nameof(MailCopy.Id)}, '_') > 0
  AND SUBSTR({nameof(MailCopy.Id)}, INSTR({nameof(MailCopy.Id)}, '_') + 1) <> ''
  AND SUBSTR({nameof(MailCopy.Id)}, INSTR({nameof(MailCopy.Id)}, '_') + 1) NOT GLOB '*[^0-9]*'").ConfigureAwait(false);
        }

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.ImapUidValidity)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.ImapUidValidity)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);

            await Connection.ExecuteAsync($@"
UPDATE {nameof(MailCopy)}
SET {nameof(MailCopy.ImapUidValidity)} = COALESCE((
    SELECT {nameof(MailItemFolder.UidValidity)}
    FROM {nameof(MailItemFolder)}
    WHERE {nameof(MailItemFolder)}.{nameof(MailItemFolder.Id)} = {nameof(MailCopy)}.{nameof(MailCopy.FolderId)}
), 0)
WHERE {nameof(MailCopy.ImapUid)} > 0").ConfigureAwait(false);
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

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.AuthenticationAddress)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.AuthenticationAddress)} TEXT NULL")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsProtocolLogEnabled)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsProtocolLogEnabled)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsContactAccessGranted)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsContactAccessGranted)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsContactReauthorizationRequired)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsContactReauthorizationRequired)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsTaskAccessGranted)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsTaskAccessGranted)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsTaskReauthorizationRequired)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsTaskReauthorizationRequired)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        // AccountTaskList uses a stable storage name so the schema remains compatible
        // with the task synchronizer and account cleanup SQL.
        const string taskListTableName = "TaskList";
        var taskListColumns = await Connection.GetTableInfoAsync(taskListTableName).ConfigureAwait(false);
        if (!taskListColumns.Any(c => c.Name == nameof(AccountTaskList.ListDeltaLink)))
        {
            await Connection.ExecuteAsync($"ALTER TABLE {taskListTableName} ADD COLUMN {nameof(AccountTaskList.ListDeltaLink)} TEXT NULL").ConfigureAwait(false);
        }

        if (!taskListColumns.Any(c => c.Name == nameof(AccountTaskList.TaskDeltaLink)))
        {
            await Connection.ExecuteAsync($"ALTER TABLE {taskListTableName} ADD COLUMN {nameof(AccountTaskList.TaskDeltaLink)} TEXT NULL").ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.ProfilePictureFileId)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.ProfilePictureFileId)} TEXT NULL")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsProfilePictureBackfillComplete)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsProfilePictureBackfillComplete)} INTEGER NOT NULL DEFAULT 0")
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

        if (!folderColumns.Any(c => c.Name == nameof(MailItemFolder.IsJumpListEnabled)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailItemFolder)} ADD COLUMN {nameof(MailItemFolder.IsJumpListEnabled)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);

            await Connection
                .ExecuteAsync($"UPDATE {nameof(MailItemFolder)} SET {nameof(MailItemFolder.IsJumpListEnabled)} = 1 WHERE {nameof(MailItemFolder.SpecialFolderType)} = {(int)SpecialFolderType.Inbox}")
                .ConfigureAwait(false);
        }

        var accountPreferencesColumns = await Connection.GetTableInfoAsync(nameof(MailAccountPreferences)).ConfigureAwait(false);

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.IsJumpListEnabled)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.IsJumpListEnabled)} INTEGER NOT NULL DEFAULT 1")
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

        if (!customServerColumns.Any(c => c.Name == nameof(CustomServerInformation.ConnectionPolicyVersion)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CustomServerInformation)} ADD COLUMN {nameof(CustomServerInformation.ConnectionPolicyVersion)} INTEGER NOT NULL DEFAULT {(int)ImapConnectionPolicyVersion.Legacy}")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.IsSemanticIndexingEnabled)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.IsSemanticIndexingEnabled)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.IsDailyBriefingEnabled)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.IsDailyBriefingEnabled)} INTEGER NOT NULL DEFAULT 1")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.ExcludedIntelligenceIndicatorIdsStorage)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.ExcludedIntelligenceIndicatorIdsStorage)} TEXT NOT NULL DEFAULT ''")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.IsIntelligenceFolderSelectionInitialized)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.IsIntelligenceFolderSelectionInitialized)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.SelectedIntelligenceFolderIdsStorage)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.SelectedIntelligenceFolderIdsStorage)} TEXT NOT NULL DEFAULT ''")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.IsIntelligenceCoverageInitialized)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.IsIntelligenceCoverageInitialized)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.IntelligenceFolderCoverageStorage)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.IntelligenceFolderCoverageStorage)} TEXT NOT NULL DEFAULT ''")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.IntelligenceDefaultCoverageStorage)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.IntelligenceDefaultCoverageStorage)} TEXT NOT NULL DEFAULT ''")
                .ConfigureAwait(false);
        }

        if (!accountPreferencesColumns.Any(c => c.Name == nameof(MailAccountPreferences.AutomaticallyIndexNewMessages)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccountPreferences)} ADD COLUMN {nameof(MailAccountPreferences.AutomaticallyIndexNewMessages)} INTEGER NOT NULL DEFAULT 1")
                .ConfigureAwait(false);
        }

        var calendarItemColumns = await Connection.GetTableInfoAsync(nameof(CalendarItem)).ConfigureAwait(false);

        if (!calendarItemColumns.Any(c => c.Name == nameof(CalendarItem.SnoozedUntil)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CalendarItem)} ADD COLUMN {nameof(CalendarItem.SnoozedUntil)} TEXT NULL")
                .ConfigureAwait(false);
        }

        var thumbnailColumns = await Connection.GetTableInfoAsync(nameof(Thumbnail)).ConfigureAwait(false);

        var isThumbnailFileCacheMigrationNeeded = false;

        if (!thumbnailColumns.Any(c => c.Name == nameof(Thumbnail.GravatarFileName)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(Thumbnail)} ADD COLUMN {nameof(Thumbnail.GravatarFileName)} TEXT NULL")
                .ConfigureAwait(false);

            isThumbnailFileCacheMigrationNeeded = true;
        }

        if (!thumbnailColumns.Any(c => c.Name == nameof(Thumbnail.FaviconFileName)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(Thumbnail)} ADD COLUMN {nameof(Thumbnail.FaviconFileName)} TEXT NULL")
                .ConfigureAwait(false);

            isThumbnailFileCacheMigrationNeeded = true;
        }

        if (isThumbnailFileCacheMigrationNeeded)
        {
            await Connection.DeleteAllAsync<Thumbnail>().ConfigureAwait(false);
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

        await Connection.ExecuteAsync(@"
UPDATE ContactCard
SET SortKey = LOWER(COALESCE(
    NULLIF(TRIM(DisplayName), ''),
    NULLIF(TRIM(CompanyName), ''),
    (SELECT e.Address FROM ContactEmailAddress e WHERE e.ContactId = ContactCard.Id ORDER BY e.IsPrimary DESC, e.""Order"" LIMIT 1),
    (SELECT p.Number FROM ContactPhoneNumber p WHERE p.ContactId = ContactCard.Id ORDER BY p.IsPrimary DESC, p.""Order"" LIMIT 1),
    ''))
WHERE SortKey IS NULL OR SortKey = ''").ConfigureAwait(false);
    }

    private async Task EnsureWinoAccountSchemaAsync()
    {
        var columns = await Connection.GetTableInfoAsync(nameof(WinoAccount)).ConfigureAwait(false);
        if (columns.Any(c => c.Name == nameof(WinoAccount.IsUnlimitedAccountsEnabled)))
        {
            return;
        }

        await Connection.ExecuteAsync(
            $"ALTER TABLE {nameof(WinoAccount)} ADD COLUMN {nameof(WinoAccount.IsUnlimitedAccountsEnabled)} INTEGER NOT NULL DEFAULT 0")
            .ConfigureAwait(false);
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
        // Contact indexes
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_ContactEmailAddress_NormalizedAddress ON ContactEmailAddress(NormalizedAddress)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_ContactEmailAddress_Contact_Normalized ON ContactEmailAddress(ContactId, NormalizedAddress) WHERE NormalizedAddress IS NOT NULL AND NormalizedAddress <> ''").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_ContactCard_Account_Source_Book ON ContactCard(MailAccountId, SourceKind, AddressBookId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_ContactCard_RemoteIdentity ON ContactCard(MailAccountId, SourceKind, AddressBookId, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_ContactCard_SortKey ON ContactCard(SortKey, Id)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_ContactAddressBook_Account_Source ON ContactAddressBook(MailAccountId, SourceKind)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_ContactAddressBook_Account_DeltaToken ON ContactAddressBook(MailAccountId, DeltaToken)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_ContactCard_IsFavorite_SortKey ON ContactCard(IsFavorite, SortKey)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_ContactListMember_List_Contact ON ContactListMember(ListId, ContactId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_ContactListMember_ContactId ON ContactListMember(ContactId)").ConfigureAwait(false);

        // Task indexes
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskList_AccountId ON TaskList(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskList_Account_Source ON TaskList(MailAccountId, SourceKind)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskList_RemoteIdentity ON TaskList(MailAccountId, SourceKind, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskCard_Account_List ON TaskCard(MailAccountId, TaskListId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskCard_RemoteIdentity ON TaskCard(MailAccountId, SourceKind, TaskListId, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskCard_Due_Completed ON TaskCard(DueDate, IsCompleted)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskStep_Task_Order ON TaskStep(TaskId, [Order])").ConfigureAwait(false);

        // Mail indexes
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_Id ON MailCopy(Id)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId ON MailCopy(FolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_Id_FolderId ON MailCopy(Id, FolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId_ImapUid ON MailCopy(FolderId, ImapUidValidity, ImapUid)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_ThreadId ON MailCopy(ThreadId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_MessageId ON MailCopy(MessageId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId_IsRead ON MailCopy(FolderId, IsRead)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_CreationDate ON MailCopy(CreationDate)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId_IsPinned_CreationDate_UniqueId ON MailCopy(FolderId, IsPinned, CreationDate DESC, UniqueId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategory_MailAccountId ON MailCategory(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategory_MailAccountId_Name ON MailCategory(MailAccountId, Name)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategory_MailAccountId_IsFavorite ON MailCategory(MailAccountId, IsFavorite)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategoryAssignment_MailCategoryId ON MailCategoryAssignment(MailCategoryId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCategoryAssignment_MailCopyUniqueId ON MailCategoryAssignment(MailCopyUniqueId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_MailCategoryAssignment_Category_MailCopy ON MailCategoryAssignment(MailCategoryId, MailCopyUniqueId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailFilter_Account_Type_Sequence ON MailFilter(MailAccountId, ManagementType, Sequence)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailFilter_Account_Source ON MailFilter(MailAccountId, SourceRemoteFolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_MailFilter_Account_RemoteId ON MailFilter(MailAccountId, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailFilterCondition_FilterId ON MailFilterCondition(MailFilterId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailFilterAction_FilterId ON MailFilterAction(MailFilterId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_MailFilterExecution_Filter_Message_Source ON MailFilterExecution(MailFilterId, RemoteMessageId, SourceRemoteFolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_AccountProviderFeature_Account_Feature ON AccountProviderFeature(MailAccountId, Feature)").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId ON MailItemFolder(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_RemoteFolderId ON MailItemFolder(MailAccountId, RemoteFolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_ParentRemoteFolderId ON MailItemFolder(MailAccountId, ParentRemoteFolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailItemFolder_MailAccountId_SpecialFolderType ON MailItemFolder(MailAccountId, SpecialFolderType)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_FolderConfigurationOverride_MailAccountId_RemoteFolderId ON FolderConfigurationOverride(MailAccountId, RemoteFolderId)").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccount_MergedInboxId ON MailAccount(MergedInboxId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccount_Order ON MailAccount([Order])").ConfigureAwait(false);

        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_AccountSignature_MailAccountId ON AccountSignature(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_EmailTemplate_Name ON EmailTemplate(Name)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccountAlias_AccountId ON MailAccountAlias(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailAccountAlias_AccountId_AliasAddress ON MailAccountAlias(AccountId, AliasAddress)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_MailAccountPreferences_AccountId ON MailAccountPreferences(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_CustomServerInformation_AccountId ON CustomServerInformation(AccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync($"CREATE UNIQUE INDEX IF NOT EXISTS IX_MailServerCertificateTrust_Endpoint ON {nameof(MailServerCertificateTrust)}({nameof(MailServerCertificateTrust.AccountId)}, {nameof(MailServerCertificateTrust.Protocol)}, {nameof(MailServerCertificateTrust.Host)}, {nameof(MailServerCertificateTrust.Port)})").ConfigureAwait(false);
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

    private async Task MigrateLegacyContactsAsync()
    {
        var legacyColumns = await Connection.GetTableInfoAsync("AccountContact").ConfigureAwait(false);
        if (legacyColumns.Count == 0)
            return;

        var legacyPictureIds = new List<Guid>();
        if (legacyColumns.Any(column => column.Name == "ContactPictureFileId"))
        {
            var values = await Connection.QueryScalarsAsync<string>(
                "SELECT ContactPictureFileId FROM AccountContact WHERE ContactPictureFileId IS NOT NULL AND ContactPictureFileId <> ''")
                .ConfigureAwait(false);

            legacyPictureIds.AddRange(values.Where(value => Guid.TryParse(value, out _)).Select(Guid.Parse));
        }

        await Connection.RunInTransactionAsync(connection =>
        {
            connection.Execute("DROP TABLE IF EXISTS ContactGroupMember");
            connection.Execute("DROP TABLE IF EXISTS ContactGroup");
            connection.Execute("DROP TABLE IF EXISTS AccountContact");
        }).ConfigureAwait(false);

        var contactsRoot = string.IsNullOrWhiteSpace(_folderConfiguration.ApplicationDataFolderPath)
            ? _folderConfiguration.PublisherSharedFolderPath
            : _folderConfiguration.ApplicationDataFolderPath;
        var contactsFolder = Path.Combine(contactsRoot, "contacts");
        foreach (var pictureId in legacyPictureIds)
        {
            try
            {
                var picturePath = Path.Combine(contactsFolder, $"{pictureId}.jpg");
                if (File.Exists(picturePath))
                    File.Delete(picturePath);
            }
            catch
            {
                // Contact rows are intentionally discarded even if an orphaned cache file is locked.
            }
        }
    }

    private async Task EnsureLocalAddressBooksAsync()
    {
        var accounts = await Connection.Table<MailAccount>().ToListAsync().ConfigureAwait(false);
        var books = await Connection.Table<ContactAddressBook>().ToListAsync().ConfigureAwait(false);
        var existingAccountIds = books.Select(book => book.MailAccountId).ToHashSet();
        var missingBooks = accounts
            .Where(account => !account.IsContactAccessGranted && !existingAccountIds.Contains(account.Id))
            .Select(account => new ContactAddressBook
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                SourceKind = ContactSourceKind.Local,
                DisplayName = account.Name,
                IsDefault = true
            })
            .ToList();

        if (missingBooks.Count > 0)
        {
            await Connection.RunInTransactionAsync(transaction =>
            {
                foreach (var book in missingBooks)
                {
                    transaction.Execute(
                        "INSERT INTO ContactAddressBook (Id, MailAccountId, SourceKind, RemoteId, ParentRemoteId, DisplayName, IsDefault, DeltaToken, LastSuccessfulSyncUtc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                        book.Id, book.MailAccountId, book.SourceKind, book.RemoteId, book.ParentRemoteId,
                        book.DisplayName, book.IsDefault, book.DeltaToken, book.LastSuccessfulSyncUtc);
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task EnsureLocalTaskListsAsync()
    {
        var accounts = await Connection.Table<MailAccount>().ToListAsync().ConfigureAwait(false);
        var lists = await Connection.Table<AccountTaskList>().ToListAsync().ConfigureAwait(false);
        var existingAccountIds = lists
            .Where(list => list.SourceKind == TaskSourceKind.Local)
            .Select(list => list.MailAccountId)
            .ToHashSet();
        var missingLists = accounts
            .Where(account => account.ProviderType == MailProviderType.IMAP4 && !existingAccountIds.Contains(account.Id))
            .Select(account => new AccountTaskList
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                SourceKind = TaskSourceKind.Local,
                Title = string.IsNullOrWhiteSpace(account.Name) ? "Tasks" : account.Name,
                IsDefault = true,
                PendingMutation = TaskPendingMutation.None
            })
            .ToList();

        if (missingLists.Count == 0)
            return;

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var list in missingLists)
            {
                    transaction.Execute(
                        "INSERT INTO TaskList (Id, MailAccountId, SourceKind, RemoteId, RemoteVersion, ListDeltaLink, TaskDeltaLink, Title, IsDefault, IsReadOnly, DeltaLink, LastSuccessfulSyncUtc, WatermarkUtc, PendingMutation, CreatedAtUtc, ModifiedAtUtc) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                        list.Id, list.MailAccountId, list.SourceKind, list.RemoteId, list.RemoteVersion, list.ListDeltaLink,
                        list.TaskDeltaLink, list.Title, list.IsDefault, list.IsReadOnly, list.DeltaLink, list.LastSuccessfulSyncUtc, list.WatermarkUtc,
                        list.PendingMutation, list.CreatedAtUtc, list.ModifiedAtUtc);
            }
        }).ConfigureAwait(false);
    }
}
