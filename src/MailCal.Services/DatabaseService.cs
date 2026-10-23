using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SQLite;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Misc;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Migration;

namespace MailCal.Services;

public interface IDatabaseService : IInitializeAsync
{
    SQLiteAsyncConnection Connection { get; }
}

/// <summary>
/// What a version-210 database file says about itself, read without changing it.
/// </summary>
/// <param name="ReadError">Set when the file exists but could not be read; the other values are then unknown.</param>
public sealed record DatabaseLifecycleSnapshot(
    bool Exists,
    int SchemaVersion,
    bool HasMetadataTable,
    MigrationStatus? MetadataStatus,
    long AccountCount,
    Exception ReadError)
{
    public static DatabaseLifecycleSnapshot Missing { get; } = new(false, 0, false, null, 0, null);

    public bool IsReadable => Exists && ReadError == null;

    /// <summary>A database that finished its migration or its fresh creation and can start normally.</summary>
    public bool IsReady => IsReadable &&
                           SchemaVersion == DatabaseService.CurrentSchemaVersion &&
                           MetadataStatus is MigrationStatus.Completed or MigrationStatus.Skipped;

    /// <summary>The file exists but is not a SQLite database or is damaged, so nothing in it can be read.</summary>
    public bool IsDamaged => ReadError is SQLiteException { Result: SQLite3.Result.NonDBFile or SQLite3.Result.Corrupt };

    /// <summary>Not ready, but holds no account: an interrupted first launch or fresh start.</summary>
    public bool IsDisposable => IsReadable && !IsReady && AccountCount == 0;
}

public class DatabaseService : IDatabaseService
{
    public const string LegacyDatabaseName = "MailCal200.db";
    public const string CurrentDatabaseName = "MailCal210.db";
    public const int CurrentSchemaVersion = 210;

    /// <summary>
    /// Recipient history upserts target this constraint, so every database that holds the table needs it.
    /// </summary>
    public const string RecipientHistoryUniqueIndexSql =
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_RecipientHistory_Account_Address ON RecipientHistory(AccountId, NormalizedAddress)";

    private readonly object _initializationLock = new();
    private Task _initializationTask;
    private bool _isInitialized = false;
    private bool _cardDavCreationCapabilityMigrationRequired;
    private bool _cardDavResourceModelMigrationRequired;
    private bool _contactOrganizationMigrationRequired;
    private bool _countedFolderSeedRequired;
    private bool _calendarDirectJoinLinkBackfillRequired;
    private readonly IApplicationConfiguration _folderConfiguration;
    private readonly string _databaseName;

    public SQLiteAsyncConnection Connection { get; private set; }

    public DatabaseService(IApplicationConfiguration folderConfiguration)
        : this(folderConfiguration, CurrentDatabaseName)
    {
    }

    public DatabaseService(IApplicationConfiguration folderConfiguration, string databaseName)
    {
        _folderConfiguration = folderConfiguration;
        _databaseName = string.IsNullOrWhiteSpace(databaseName)
            ? throw new ArgumentException("A database file name is required.", nameof(databaseName))
            : databaseName;
    }

    /// <summary>
    /// Initializes the database once. Concurrent callers share the same initialization task, so a second caller
    /// never replaces or closes the connection while the first one is still creating tables. A failed attempt
    /// is retried by the next caller.
    /// </summary>
    public Task InitializeAsync()
    {
        lock (_initializationLock)
        {
            if (_isInitialized)
                return Task.CompletedTask;

            if (_initializationTask == null || _initializationTask.IsFaulted || _initializationTask.IsCanceled)
                _initializationTask = InitializeCoreAsync();

            return _initializationTask;
        }
    }

    private async Task InitializeCoreAsync()
    {
        var databaseFileName = MainDatabasePaths.GetPath(_folderConfiguration, _databaseName);
        Directory.CreateDirectory(MainDatabasePaths.GetRoot(_folderConfiguration));
        var databaseAlreadyExists = File.Exists(databaseFileName);

        // Validate an existing 210 database on a private handle first. sqlite-net pools connections by path and
        // open flags, so closing a handle with the main connection's flags would close the live shared connection.
        if (databaseAlreadyExists && string.Equals(_databaseName, CurrentDatabaseName, StringComparison.OrdinalIgnoreCase))
            await EnsureCompleted210DatabaseAsync(databaseFileName).ConfigureAwait(false);

        // The main connection is created once and never replaced, so callers that already hold it keep working.
        Connection ??= new SQLiteAsyncConnection(databaseFileName);
        await Connection.ExecuteAsync("PRAGMA foreign_keys = ON;").ConfigureAwait(false);

        var preCreateCardDavAccountColumns = await Connection.GetTableInfoAsync(nameof(CardDavAccountState)).ConfigureAwait(false);
        _cardDavCreationCapabilityMigrationRequired = preCreateCardDavAccountColumns.Count > 0 &&
            !preCreateCardDavAccountColumns.Any(column => column.Name == nameof(CardDavAccountState.SupportsAddressBookCreation));

        // Resources stored before group vCards were recognized carry no kind: groups sit in the
        // contact table. Sampled before CreateTablesAsync adds the column.
        var preCreateCardDavResourceColumns = await Connection.GetTableInfoAsync(nameof(CardDavResourceShadow)).ConfigureAwait(false);
        _cardDavResourceModelMigrationRequired = preCreateCardDavResourceColumns.Count > 0 &&
            !preCreateCardDavResourceColumns.Any(column => column.Name == nameof(CardDavResourceShadow.Kind));

        // Contacts stored before lists and categories were synchronized carry neither. Sampled
        // before CreateTablesAsync adds the column.
        var preCreateContactListColumns = await Connection.GetTableInfoAsync(nameof(ContactList)).ConfigureAwait(false);
        _contactOrganizationMigrationRequired = preCreateContactListColumns.Count > 0 &&
            !preCreateContactListColumns.Any(column => column.Name == nameof(ContactList.RemoteId));

        // The counted-folder flag has to be sampled before CreateTablesAsync adds the column with a 0 default,
        // otherwise every existing account would silently stop counting its Inbox.
        var preCreateFolderColumns = await Connection.GetTableInfoAsync(nameof(MailItemFolder)).ConfigureAwait(false);
        _countedFolderSeedRequired = preCreateFolderColumns.Count > 0 &&
            !preCreateFolderColumns.Any(column => column.Name == nameof(MailItemFolder.IsCountedInAccountTotal));
        var preCreateCalendarItemColumns = await Connection.GetTableInfoAsync(nameof(CalendarItem)).ConfigureAwait(false);
        _calendarDirectJoinLinkBackfillRequired = preCreateCalendarItemColumns.Count > 0 &&
            !preCreateCalendarItemColumns.Any(column => column.Name == nameof(CalendarItem.DirectJoinLink));
        await CreateTablesAsync();
        await Connection.ExecuteAsync($"PRAGMA user_version = {CurrentSchemaVersion};").ConfigureAwait(false);
        await EnsureLifecycleMetadataAsync(databaseAlreadyExists).ConfigureAwait(false);

        lock (_initializationLock)
        {
            _isInitialized = true;
        }
    }

    private static async Task EnsureCompleted210DatabaseAsync(string databaseFileName)
    {
        var lifecycle = await ReadLifecycleAsync(databaseFileName).ConfigureAwait(false);

        if (!lifecycle.IsReady)
        {
            throw new InvalidOperationException(
                "MailCal210.db is not a completed version-210 database. Run the migration coordinator before normal startup.",
                lifecycle.ReadError);
        }
    }

    /// <summary>
    /// Reads whether a version-210 database is ready for normal use. Startup and the migration
    /// coordinator both decide with this one rule, so a database that starts normally never sends
    /// the user to the migration window. Integrity and foreign-key checks belong to the migration's
    /// own output, not to a database the user has been working in.
    /// </summary>
    public static async Task<DatabaseLifecycleSnapshot> ReadLifecycleAsync(string databaseFileName)
    {
        if (!File.Exists(databaseFileName))
            return DatabaseLifecycleSnapshot.Missing;

        Exception lastError = null;

        // Another Mail-Cal process can hold a write lock for a moment while it closes. That is not a
        // reason to treat the database as broken, so a busy database is read again before giving up.
        for (var attempt = 0; attempt < LifecycleReadAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(LifecycleReadRetryDelay).ConfigureAwait(false);

            // Distinct flags give this handle its own pool entry; closing it leaves the main connection open.
            var connection = new SQLiteAsyncConnection(
                databaseFileName,
                SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex | SQLiteOpenFlags.PrivateCache);

            try
            {
                await connection.SetBusyTimeoutAsync(LifecycleBusyTimeout).ConfigureAwait(false);

                var version = await connection.ExecuteScalarAsync<int>("PRAGMA user_version;").ConfigureAwait(false);
                var hasMetadataTable = await TableExistsAsync(connection, "__MigrationMetadata").ConfigureAwait(false);
                var metadataStatuses = hasMetadataTable
                    ? await connection.QueryScalarsAsync<int>("SELECT Status FROM __MigrationMetadata WHERE Id = 1;").ConfigureAwait(false)
                    : new List<int>();
                MigrationStatus? metadataStatus = metadataStatuses.Count > 0 ? (MigrationStatus)metadataStatuses[0] : null;
                var accountCount = await TableExistsAsync(connection, nameof(MailAccount)).ConfigureAwait(false)
                    ? await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {nameof(MailAccount)};").ConfigureAwait(false)
                    : 0;

                return new DatabaseLifecycleSnapshot(true, version, hasMetadataTable, metadataStatus, accountCount, null);
            }
            catch (Exception ex) when (ex is SQLiteException or IOException or UnauthorizedAccessException)
            {
                lastError = ex;

                if (ex is not SQLiteException { Result: SQLite3.Result.Busy or SQLite3.Result.Locked })
                    break;
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        return new DatabaseLifecycleSnapshot(true, 0, false, null, 0, lastError);
    }

    private const int LifecycleReadAttempts = 3;
    private static readonly TimeSpan LifecycleReadRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LifecycleBusyTimeout = TimeSpan.FromSeconds(1);

    private static async Task<bool> TableExistsAsync(SQLiteAsyncConnection connection, string tableName)
        => await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=?;",
            tableName).ConfigureAwait(false) == 1;

    private async Task EnsureLifecycleMetadataAsync(bool databaseAlreadyExists)
    {
        await Connection.ExecuteAsync(@"
CREATE TABLE IF NOT EXISTS __MigrationMetadata (
    Id INTEGER PRIMARY KEY NOT NULL,
    SourcePath TEXT,
    LastCompletedStep INTEGER NOT NULL,
    Status INTEGER NOT NULL,
    OptionsJson TEXT,
    RowCounts TEXT,
    DeferredAccountIds TEXT,
    UpdatedAtUtc TEXT NOT NULL
);").ConfigureAwait(false);

        if (!databaseAlreadyExists)
        {
            await Connection.ExecuteAsync(@"
INSERT OR REPLACE INTO __MigrationMetadata
    (Id, SourcePath, LastCompletedStep, Status, OptionsJson, RowCounts, DeferredAccountIds, UpdatedAtUtc)
VALUES
    (1, '', ?, ?, '', '', '', ?);",
                (int)MigrationStepKind.Completed,
                (int)MigrationStatus.Skipped,
                DateTime.UtcNow).ConfigureAwait(false);
        }
    }

    private async Task CreateTablesAsync()
    {
        await Task.WhenAll(
            Connection.CreateTableAsync<MailCopy>(),
            Connection.CreateTableAsync<Pop3PendingServerDeletion>(),
            Connection.CreateTableAsync<Pop3RemoteMessageState>(),
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
            Connection.CreateTableAsync<ContactCategoryAssignment>(),
            Connection.CreateTableAsync<RecipientHistory>(),
            Connection.CreateTableAsync<CardDavAccountState>(),
            Connection.CreateTableAsync<CardDavAddressBookState>(),
            Connection.CreateTableAsync<CardDavResourceShadow>(),
            Connection.CreateTableAsync<AccountTaskListGroup>(),
            Connection.CreateTableAsync<AccountTaskSyncState>(),
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
            Connection.CreateTableAsync<KeyboardShortcut>(),
            Connection.CreateTableAsync<AccountCalendar>(),
            Connection.CreateTableAsync<CalendarEventAttendee>(),
            Connection.CreateTableAsync<CalendarItem>(),
            Connection.CreateTableAsync<CalendarAttachment>(),
            Connection.CreateTableAsync<Reminder>(),
            Connection.CreateTableAsync<MailInvitationCalendarMapping>(),
            Connection.CreateTableAsync<SentMailReceiptState>());

        await EnsureSchemaUpgradesAsync().ConfigureAwait(false);
        await EnsureIndexesAsync().ConfigureAwait(false);
        await EnsureLocalAddressBooksAsync().ConfigureAwait(false);
        await AssignOwnerlessContactListsAsync().ConfigureAwait(false);
        await EnsureLocalTaskListsAsync().ConfigureAwait(false);
    }

    private async Task EnsureSchemaUpgradesAsync()
    {
        await EnsureKeyboardShortcutSchemaAsync().ConfigureAwait(false);
        // online account sign-in was removed; drop the table and any session it still holds.
        await Connection.ExecuteAsync("DROP TABLE IF EXISTS WinoAccount").ConfigureAwait(false);

        if (_countedFolderSeedRequired)
        {
            await Connection.ExecuteAsync(
                $"UPDATE {nameof(MailItemFolder)} SET {nameof(MailItemFolder.IsCountedInAccountTotal)} = {nameof(MailItemFolder.ShowUnreadCount)}")
                .ConfigureAwait(false);
        }

        if (_cardDavCreationCapabilityMigrationRequired)
        {
            await Connection.ExecuteAsync(
                $"UPDATE {nameof(CardDavAccountState)} SET {nameof(CardDavAccountState.RequiresRediscovery)} = 1")
                .ConfigureAwait(false);
        }

        if (_contactOrganizationMigrationRequired)
        {
            // Forget every contact checkpoint so the next synchronization downloads each contact
            // again, now with its lists and categories.
            await Connection.ExecuteAsync($"UPDATE {nameof(ContactAddressBook)} SET {nameof(ContactAddressBook.DeltaToken)} = NULL").ConfigureAwait(false);
        }

        if (_cardDavResourceModelMigrationRequired || _contactOrganizationMigrationRequired)
        {
            // Forget every version and checkpoint so the next synchronization downloads each
            // resource again and sorts it into contacts and lists.
            await Connection.ExecuteAsync($"UPDATE {nameof(CardDavResourceShadow)} SET {nameof(CardDavResourceShadow.ETag)} = NULL").ConfigureAwait(false);
            await Connection.ExecuteAsync(
                $"UPDATE {nameof(CardDavAddressBookState)} SET {nameof(CardDavAddressBookState.SyncToken)} = NULL, {nameof(CardDavAddressBookState.CollectionTag)} = NULL")
                .ConfigureAwait(false);
        }

        // Left behind by the retired CardDAV outbox and conflict resolver.
        await Connection.ExecuteAsync("DROP TABLE IF EXISTS CardDavOutboxItem").ConfigureAwait(false);
        await Connection.ExecuteAsync("DROP TABLE IF EXISTS CardDavConflict").ConfigureAwait(false);
        await Connection.ExecuteAsync("DROP TABLE IF EXISTS CardDavQuarantine").ConfigureAwait(false);
        await Connection.ExecuteAsync("DROP INDEX IF EXISTS IX_CardDavResourceShadow_Book_Generation").ConfigureAwait(false);

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
            await Connection.ExecuteAsync("ALTER TABLE MailCopy ADD COLUMN ImapUid INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.ImapUidValidity)))
            await Connection.ExecuteAsync("ALTER TABLE MailCopy ADD COLUMN ImapUidValidity INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);

        // SQLite-net can add columns before this migration runs. Backfill zero/NULL values as well,
        // including databases upgraded by earlier versions. Keep identity, MIME files and user state.
        await Connection.ExecuteAsync(@"
UPDATE MailCopy
SET ImapUid = CAST(SUBSTR(Id, LENGTH(FolderId) + 2) AS INTEGER)
WHERE COALESCE(ImapUid, 0) = 0
  AND SUBSTR(Id, 1, LENGTH(FolderId) + 1) = FolderId || '_'
  AND SUBSTR(Id, LENGTH(FolderId) + 2) <> ''
  AND SUBSTR(Id, LENGTH(FolderId) + 2) NOT GLOB '*[^0-9]*'
  AND CAST(SUBSTR(Id, LENGTH(FolderId) + 2) AS INTEGER) BETWEEN 1 AND 4294967295
  AND EXISTS (SELECT 1 FROM MailItemFolder f JOIN MailAccount a ON a.Id = f.MailAccountId
              WHERE f.Id = MailCopy.FolderId AND a.ProviderType = ?)", (int)MailProviderType.IMAP4).ConfigureAwait(false);

        await Connection.ExecuteAsync(@"
UPDATE MailCopy
SET ImapUidValidity = COALESCE((SELECT UidValidity FROM MailItemFolder WHERE Id = MailCopy.FolderId), 0)
WHERE ImapUid > 0 AND COALESCE(ImapUidValidity, 0) = 0
  AND EXISTS (SELECT 1 FROM MailItemFolder f JOIN MailAccount a ON a.Id = f.MailAccountId
              WHERE f.Id = MailCopy.FolderId AND a.ProviderType = ?)", (int)MailProviderType.IMAP4).ConfigureAwait(false);

        if (!mailCopyColumns.Any(c => c.Name == nameof(MailCopy.Pop3Uidl)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} ADD COLUMN {nameof(MailCopy.Pop3Uidl)} TEXT NULL")
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

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsCalendarAccessEnabled)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsCalendarAccessEnabled)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
            await Connection
                .ExecuteAsync($"UPDATE {nameof(MailAccount)} SET {nameof(MailAccount.IsCalendarAccessEnabled)} = {nameof(MailAccount.IsCalendarAccessGranted)}")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsContactAccessEnabled)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsContactAccessEnabled)} INTEGER NOT NULL DEFAULT 1")
                .ConfigureAwait(false);
        }

        var contactAddressBookColumns = await Connection.GetTableInfoAsync(nameof(ContactAddressBook)).ConfigureAwait(false);

        if (!contactAddressBookColumns.Any(c => c.Name == nameof(ContactAddressBook.IsReadOnly)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(ContactAddressBook)} ADD COLUMN {nameof(ContactAddressBook.IsReadOnly)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!contactAddressBookColumns.Any(c => c.Name == nameof(ContactAddressBook.IsPendingRemoteOperation)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(ContactAddressBook)} ADD COLUMN {nameof(ContactAddressBook.IsPendingRemoteOperation)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
        }

        if (!accountColumns.Any(c => c.Name == nameof(MailAccount.IsTaskAccessEnabled)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} ADD COLUMN {nameof(MailAccount.IsTaskAccessEnabled)} INTEGER NOT NULL DEFAULT 0")
                .ConfigureAwait(false);
            await Connection
                .ExecuteAsync($"UPDATE {nameof(MailAccount)} SET {nameof(MailAccount.IsTaskAccessEnabled)} = 1 WHERE {nameof(MailAccount.IsTaskAccessGranted)} = 1 OR {nameof(MailAccount.ProviderType)} = {(int)MailProviderType.IMAP4}")
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

        if (!taskListColumns.Any(c => c.Name == nameof(AccountTaskList.ColorHex)))
        {
            await Connection.ExecuteAsync($"ALTER TABLE {taskListTableName} ADD COLUMN {nameof(AccountTaskList.ColorHex)} TEXT NULL").ConfigureAwait(false);
        }

        if (!taskListColumns.Any(c => c.Name == nameof(AccountTaskList.GroupId)))
        {
            await Connection.ExecuteAsync($"ALTER TABLE {taskListTableName} ADD COLUMN {nameof(AccountTaskList.GroupId)} BLOB NULL").ConfigureAwait(false);
        }

        if (!taskListColumns.Any(c => c.Name == nameof(AccountTaskList.SortOrder)))
        {
            await Connection.ExecuteAsync($"ALTER TABLE {taskListTableName} ADD COLUMN {nameof(AccountTaskList.SortOrder)} INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
        }

        if (!taskListColumns.Any(c => c.Name == nameof(AccountTaskList.RemoteOrder)))
        {
            await Connection.ExecuteAsync($"ALTER TABLE {taskListTableName} ADD COLUMN {nameof(AccountTaskList.RemoteOrder)} TEXT NULL").ConfigureAwait(false);
        }

        if (!taskListColumns.Any(c => c.Name == nameof(AccountTaskList.SubstrateGroupDeltaLink)))
        {
            await Connection.ExecuteAsync($"ALTER TABLE {taskListTableName} ADD COLUMN {nameof(AccountTaskList.SubstrateGroupDeltaLink)} TEXT NULL").ConfigureAwait(false);
        }

        if (!taskListColumns.Any(c => c.Name == nameof(AccountTaskList.SubstrateFolderDeltaLink)))
        {
            await Connection.ExecuteAsync($"ALTER TABLE {taskListTableName} ADD COLUMN {nameof(AccountTaskList.SubstrateFolderDeltaLink)} TEXT NULL").ConfigureAwait(false);
        }

        var taskGroupColumns = await Connection.GetTableInfoAsync("TaskListGroup").ConfigureAwait(false);
        if (!taskGroupColumns.Any(c => c.Name == nameof(AccountTaskListGroup.RemoteVersion)))
            await Connection.ExecuteAsync($"ALTER TABLE TaskListGroup ADD COLUMN {nameof(AccountTaskListGroup.RemoteVersion)} TEXT NULL").ConfigureAwait(false);
        if (!taskGroupColumns.Any(c => c.Name == nameof(AccountTaskListGroup.RemoteOrder)))
            await Connection.ExecuteAsync($"ALTER TABLE TaskListGroup ADD COLUMN {nameof(AccountTaskListGroup.RemoteOrder)} TEXT NULL").ConfigureAwait(false);
        if (!taskGroupColumns.Any(c => c.Name == nameof(AccountTaskListGroup.PendingMutation)))
            await Connection.ExecuteAsync($"ALTER TABLE TaskListGroup ADD COLUMN {nameof(AccountTaskListGroup.PendingMutation)} INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);

        await BackfillTaskSyncStateAsync().ConfigureAwait(false);

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

        if (!customServerColumns.Any(c => c.Name == nameof(CustomServerInformation.CardDavServiceUrl)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CustomServerInformation)} ADD COLUMN {nameof(CustomServerInformation.CardDavServiceUrl)} TEXT NULL")
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

        if (!calendarItemColumns.Any(c => c.Name == nameof(CalendarItem.DirectJoinLink)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CalendarItem)} ADD COLUMN {nameof(CalendarItem.DirectJoinLink)} TEXT NULL")
                .ConfigureAwait(false);
        }

        if (_calendarDirectJoinLinkBackfillRequired)
        {
            var calendarItems = await Connection.Table<CalendarItem>().ToListAsync().ConfigureAwait(false);
            await Connection.RunInTransactionAsync(connection =>
            {
                foreach (var calendarItem in calendarItems)
                {
                    var directJoinLink = CalendarJoinLinkResolver.ResolveDirectJoinLink(null, calendarItem.Description);
                    if (!string.IsNullOrWhiteSpace(directJoinLink))
                    {
                        connection.Execute(
                            $"UPDATE {nameof(CalendarItem)} SET {nameof(CalendarItem.DirectJoinLink)} = ? WHERE {nameof(CalendarItem.Id)} = ?",
                            directJoinLink,
                            calendarItem.Id);
                    }
                }
            }).ConfigureAwait(false);
        }

        if (!calendarItemColumns.Any(c => c.Name == nameof(CalendarItem.SnoozedUntil)))
        {
            await Connection
                .ExecuteAsync($"ALTER TABLE {nameof(CalendarItem)} ADD COLUMN {nameof(CalendarItem.SnoozedUntil)} TEXT NULL")
                .ConfigureAwait(false);
        }

        // Gravatar and favicon lookups were removed; drop their cache table.
        await Connection.ExecuteAsync("DROP TABLE IF EXISTS Thumbnail").ConfigureAwait(false);

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
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_ContactCategoryAssignment_Category_Contact ON ContactCategoryAssignment(MailCategoryId, ContactId)").ConfigureAwait(false);
        await Connection.ExecuteAsync(RecipientHistoryUniqueIndexSql).ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_CardDavResourceShadow_Book_Href ON CardDavResourceShadow(AddressBookId, ExactHref)").ConfigureAwait(false);

        // Task indexes
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskList_AccountId ON TaskList(MailAccountId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskList_Group_Order ON TaskList(GroupId, SortOrder)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskListGroup_Account_Order ON TaskListGroup(MailAccountId, SortOrder)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskList_Account_Source ON TaskList(MailAccountId, SourceKind)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskList_RemoteIdentity ON TaskList(MailAccountId, SourceKind, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskSyncState_Account_Source ON TaskSyncState(MailAccountId, SourceKind)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskListGroup_RemoteIdentity ON TaskListGroup(MailAccountId, SourceKind, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskCard_Account_List ON TaskCard(MailAccountId, TaskListId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_TaskCard_RemoteIdentity ON TaskCard(MailAccountId, SourceKind, TaskListId, RemoteId) WHERE RemoteId IS NOT NULL AND RemoteId <> ''").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskCard_Due_Completed ON TaskCard(DueDate, IsCompleted)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_TaskStep_Task_Order ON TaskStep(TaskId, [Order])").ConfigureAwait(false);

        // Mail indexes
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_Id ON MailCopy(Id)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId ON MailCopy(FolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_Id_FolderId ON MailCopy(Id, FolderId)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId_ImapUid ON MailCopy(FolderId, ImapUidValidity, ImapUid)").ConfigureAwait(false);
        await Connection.ExecuteAsync("CREATE INDEX IF NOT EXISTS IX_MailCopy_FolderId_Pop3Uidl ON MailCopy(FolderId, Pop3Uidl)").ConfigureAwait(false);
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

    private async Task BackfillTaskSyncStateAsync()
    {
        var lists = await Connection.Table<AccountTaskList>().ToListAsync().ConfigureAwait(false);
        var states = await Connection.Table<AccountTaskSyncState>().ToListAsync().ConfigureAwait(false);

        foreach (var accountLists in lists
                     .Where(list => list.SourceKind is TaskSourceKind.Outlook or TaskSourceKind.Gmail)
                     .GroupBy(list => (list.MailAccountId, list.SourceKind)))
        {
            if (states.Any(state => state.MailAccountId == accountLists.Key.MailAccountId &&
                                    state.SourceKind == accountLists.Key.SourceKind))
            {
                continue;
            }

            var state = new AccountTaskSyncState
            {
                MailAccountId = accountLists.Key.MailAccountId,
                SourceKind = accountLists.Key.SourceKind,
                ListDeltaLink = accountLists.Select(list => list.ListDeltaLink).FirstOrDefault(link => !string.IsNullOrWhiteSpace(link)),
                SubstrateGroupDeltaLink = accountLists.Select(list => list.SubstrateGroupDeltaLink).FirstOrDefault(link => !string.IsNullOrWhiteSpace(link)),
                SubstrateFolderDeltaLink = accountLists.Select(list => list.SubstrateFolderDeltaLink).FirstOrDefault(link => !string.IsNullOrWhiteSpace(link)),
                LastSuccessfulSyncUtc = accountLists.Max(list => list.LastSuccessfulSyncUtc)
            };

            await Connection.InsertAsync(state, typeof(AccountTaskSyncState)).ConfigureAwait(false);
            states.Add(state);
        }
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

        var contactsRoot = MainDatabasePaths.GetRoot(_folderConfiguration);
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
            .Where(account => account.IsContactAccessEnabled &&
                              account.ContactIntegrationSource == AccountIntegrationSource.Local &&
                              !account.IsContactAccessGranted &&
                              !existingAccountIds.Contains(account.Id))
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

    /// <summary>
    /// Lists used to be free of any account. A list now belongs to one address book, so
    /// each ownerless list is handed to the local address books its members come from.
    /// Members of synchronized address books cannot be kept: nothing wrote them to a server.
    /// </summary>
    private Task AssignOwnerlessContactListsAsync()
        => Connection.RunInTransactionAsync(transaction =>
        {
            var lists = transaction.Query<ContactList>("SELECT * FROM ContactList WHERE AddressBookId IS NULL");
            if (lists.Count == 0) return;

            var localBooks = transaction.Query<ContactAddressBook>(
                "SELECT * FROM ContactAddressBook WHERE SourceKind = ?", (int)ContactSourceKind.Local).ToDictionary(book => book.Id);

            foreach (var list in lists)
            {
                var members = transaction.Query<ContactListMember>("SELECT * FROM ContactListMember WHERE ListId = ?", list.Id);
                var membersByBook = new Dictionary<Guid, List<ContactListMember>>();

                foreach (var member in members)
                {
                    var contact = transaction.Find<AccountContact>(member.ContactId);
                    if (contact is not null && localBooks.ContainsKey(contact.AddressBookId))
                    {
                        if (!membersByBook.TryGetValue(contact.AddressBookId, out var bookMembers))
                            membersByBook[contact.AddressBookId] = bookMembers = [];
                        bookMembers.Add(member);
                    }
                    else
                    {
                        transaction.Delete<ContactListMember>(member.Id);
                    }
                }

                if (membersByBook.Count == 0)
                {
                    transaction.Delete<ContactList>(list.Id);
                    continue;
                }

                var isFirst = true;
                foreach (var (bookId, bookMembers) in membersByBook)
                {
                    // The first address book keeps the list; every further one gets a copy.
                    var target = isFirst ? list : new ContactList
                    {
                        Id = Guid.NewGuid(),
                        Name = list.Name,
                        Description = list.Description,
                        ColorHex = list.ColorHex,
                        SortOrder = list.SortOrder,
                        CreatedAtUtc = list.CreatedAtUtc,
                        ModifiedAtUtc = list.ModifiedAtUtc
                    };
                    target.MailAccountId = localBooks[bookId].MailAccountId;
                    target.AddressBookId = bookId;
                    transaction.InsertOrReplace(target, typeof(ContactList));

                    if (!isFirst)
                    {
                        foreach (var member in bookMembers)
                            transaction.Execute("UPDATE ContactListMember SET ListId = ? WHERE Id = ?", target.Id, member.Id);
                    }

                    isFirst = false;
                }
            }
        });

    private async Task EnsureLocalTaskListsAsync()
    {
        var accounts = await Connection.Table<MailAccount>().ToListAsync().ConfigureAwait(false);
        var lists = await Connection.Table<AccountTaskList>().ToListAsync().ConfigureAwait(false);
        var existingAccountIds = lists
            .Where(list => list.SourceKind == TaskSourceKind.Local)
            .Select(list => list.MailAccountId)
            .ToHashSet();
        var usedColors = lists.Select(list => list.ColorHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingLists = accounts
            .Where(account => account.IsTaskAccessEnabled &&
                              account.TaskIntegrationSource == AccountIntegrationSource.Local &&
                              !account.IsTaskAccessGranted &&
                              !existingAccountIds.Contains(account.Id))
            .Select(account =>
            {
                var color = ColorPalette.GetDistinctColor(usedColors);
                usedColors.Add(color);
                return new AccountTaskList
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = account.Id,
                    SourceKind = TaskSourceKind.Local,
                    Title = string.IsNullOrWhiteSpace(account.Name) ? "Tasks" : account.Name,
                    ColorHex = color,
                    IsDefault = true,
                    PendingMutation = TaskPendingMutation.None
                };
            })
            .ToList();

        if (missingLists.Count == 0)
            return;

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var list in missingLists)
            {
                transaction.Insert(list, typeof(AccountTaskList));
            }
        }).ConfigureAwait(false);
    }
}
