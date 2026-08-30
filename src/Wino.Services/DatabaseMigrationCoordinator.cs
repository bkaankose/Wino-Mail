using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Migration;

namespace Wino.Services;

public sealed class DatabaseMigrationCoordinator : IMigrationCoordinator
{
    private static readonly TimeSpan MinimumStepVisibility = TimeSpan.FromSeconds(1);

    private static readonly string[] AccountTables =
    [
        "MergedInbox", "MailAccount", "CustomServerInformation", "AccountSignature",
        "MailServerCertificateTrust", "EmailTemplate", "MailAccountPreferences", "MailAccountAlias",
        "KeyboardShortcut", "WinoAccount"
    ];

    private static readonly string[] MailTables =
    [
        "MailItemFolder", "FolderConfigurationOverride", "MailCopy", "MailCategory", "MailCategoryAssignment",
        "Thumbnail", "SentMailReceiptState"
    ];

    private static readonly string[] CalendarTables =
    [
        "AccountCalendar", "CalendarEventAttendee", "CalendarItem", "CalendarAttachment", "Reminder",
        "MailInvitationCalendarMapping"
    ];

    private const string StagingDatabaseName = "Wino210.db.migrating";
    private const string MetadataTableName = "__MigrationMetadata";
    private const int MetadataRowId = 1;

    private readonly IApplicationConfiguration _configuration;
    private readonly IDatabaseSchemaService _schemaService;
    private readonly IAccountProfilePictureFileService _profilePictureFileService;
    private readonly IAuthenticationTokenMigrationService _authenticationTokenMigrationService;
    private readonly IMigrationClock _clock;
    private readonly SemaphoreSlim _migrationLock = new(1, 1);

    public event EventHandler<MigrationProgress> ProgressChanged;

    public DatabaseMigrationCoordinator(
        IApplicationConfiguration configuration,
        IDatabaseSchemaService schemaService,
        IAccountProfilePictureFileService profilePictureFileService,
        IAuthenticationTokenMigrationService authenticationTokenMigrationService = null,
        IMigrationClock clock = null)
    {
        _configuration = configuration;
        _schemaService = schemaService;
        _profilePictureFileService = profilePictureFileService;
        _authenticationTokenMigrationService = authenticationTokenMigrationService
            ?? new AuthenticationTokenMigrationService(configuration);
        _clock = clock ?? new SystemMigrationClock();
    }

    public async Task<MigrationPlan> InspectAsync(CancellationToken cancellationToken = default)
    {
        var sourcePath = GetPath(DatabaseService.LegacyDatabaseName);
        var stagingPath = GetPath(StagingDatabaseName);
        var destinationPath = GetPath(DatabaseService.CurrentDatabaseName);

        var invalidDestinationExists = false;
        if (File.Exists(destinationPath))
        {
            var destinationValidation = await _schemaService.ValidateAsync(
                    destinationPath,
                    cancellationToken,
                    requireCompletedMigration: true)
                .ConfigureAwait(false);
            if (destinationValidation.IsValid)
            {
                var pendingAuthorizationAccounts = await ReadPendingAuthorizationAccountsAsync(
                        destinationPath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (pendingAuthorizationAccounts.Count > 0)
                {
                    return new MigrationPlan(
                        MigrationStatus.AwaitingUser,
                        sourcePath,
                        stagingPath,
                        destinationPath,
                        true,
                        pendingAuthorizationAccounts,
                        "Some selected account features still need provider authorization.");
                }

                return new MigrationPlan(
                    MigrationStatus.NotRequired,
                    sourcePath,
                    stagingPath,
                    destinationPath,
                    false,
                    []);
            }

            invalidDestinationExists = true;
        }

        if (!File.Exists(sourcePath) && !invalidDestinationExists)
        {
            return new MigrationPlan(
                MigrationStatus.NotRequired,
                sourcePath,
                stagingPath,
                destinationPath,
                false,
                []);
        }

        List<LegacyAccountRow> accounts = [];
        string inspectionMessage = null;
        if (File.Exists(sourcePath))
        {
            try
            {
                accounts = await ReadLegacyAccountsAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                inspectionMessage = SanitizeError(ex);
            }
        }
        var options = accounts
            .OrderBy(account => account.Order)
            .Select(account => new MigrationAccountOptions(
                account.Id,
                string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name,
                account.Address,
                account.ProviderType,
                account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook,
                true,
                account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook,
                DeferSignIn: true))
            .ToList();

        return new MigrationPlan(
            MigrationStatus.Required,
            sourcePath,
            stagingPath,
            destinationPath,
            File.Exists(stagingPath),
            options,
            inspectionMessage);
    }

    public async Task<MigrationResult> RunAsync(
        IReadOnlyList<MigrationAccountOptions> accountOptions,
        CancellationToken cancellationToken = default)
    {
        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = await InspectAsync(cancellationToken).ConfigureAwait(false);
            if (plan.Status == MigrationStatus.NotRequired)
                return new MigrationResult(MigrationStatus.Completed);

            var rowCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            SQLiteAsyncConnection destination = null;
            MigrationStepKind? activeStep = null;

            try
            {
                activeStep = MigrationStepKind.CheckExistingData;
                await RunVisibleStepAsync(
                    activeStep.Value,
                    "Checking your existing data",
                    "Wino is checking the old database and available storage.",
                    () => CheckSourceAsync(plan.SourcePath, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                activeStep = MigrationStepKind.ChooseFeatures;
                await RunVisibleStepAsync(
                    activeStep.Value,
                    "Saving your feature choices",
                    "Wino will enable only the Contacts, To Do and mail-filter features you selected.",
                    () => Task.CompletedTask,
                    cancellationToken).ConfigureAwait(false);

                activeStep = MigrationStepKind.PrepareDatabase;
                destination = await RunVisibleStepAsync(
                    activeStep.Value,
                    "Preparing the new database",
                    "Wino is creating a separate version 210 database. Your old database is not changed.",
                    () => OpenOrCreateStagingAsync(plan, accountOptions, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                var metadata = await ReadMetadataAsync(destination).ConfigureAwait(false);
                await AttachLegacyAsync(destination, plan.SourcePath).ConfigureAwait(false);

                activeStep = MigrationStepKind.MigrateAccountsAndSettings;
                if (metadata.LastCompletedStep < (int)activeStep.Value)
                {
                    await RunVisibleStepAsync(
                        activeStep.Value,
                        "Moving accounts and settings",
                        "Wino is preserving account definitions, server settings, preferences, signatures and aliases.",
                        async () =>
                        {
                            await CopyTablesAsync(destination, AccountTables, rowCounts, activeStep.Value, cancellationToken)
                                .ConfigureAwait(false);
                            await SaveCheckpointAsync(destination, activeStep.Value, accountOptions).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                activeStep = MigrationStepKind.MigrateMailAndFiles;
                metadata = await ReadMetadataAsync(destination).ConfigureAwait(false);
                if (metadata.LastCompletedStep < (int)activeStep.Value)
                {
                    await RunVisibleStepAsync(
                        activeStep.Value,
                        "Moving mail and local files",
                        "Wino is preserving folders, messages, drafts, categories and local file references.",
                        async () =>
                        {
                            await CopyTablesAsync(destination, MailTables, rowCounts, activeStep.Value, cancellationToken)
                                .ConfigureAwait(false);
                            await SaveCheckpointAsync(destination, activeStep.Value, accountOptions).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                activeStep = MigrationStepKind.MigrateCalendars;
                metadata = await ReadMetadataAsync(destination).ConfigureAwait(false);
                if (metadata.LastCompletedStep < (int)activeStep.Value)
                {
                    await RunVisibleStepAsync(
                        activeStep.Value,
                        "Moving calendars",
                        "Wino is preserving calendars, events, attendees, reminders and invitation links.",
                        async () =>
                        {
                            await CopyTablesAsync(destination, CalendarTables, rowCounts, activeStep.Value, cancellationToken)
                                .ConfigureAwait(false);
                            await SaveCheckpointAsync(destination, activeStep.Value, accountOptions).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                activeStep = MigrationStepKind.ReconnectAccounts;
                metadata = await ReadMetadataAsync(destination).ConfigureAwait(false);
                if (metadata.LastCompletedStep < (int)activeStep.Value)
                {
                    await RunVisibleStepAsync(
                        activeStep.Value,
                        "Checking account sign-in",
                        "Wino is preserving reusable credentials and marking accounts that need sign-in after migration.",
                        async () =>
                        {
                            var authenticationMigration = await _authenticationTokenMigrationService
                                .PrepareAsync(accountOptions, cancellationToken)
                                .ConfigureAwait(false);
                            await ApplyAccountMappingsAsync(
                                    destination,
                                    accountOptions,
                                    authenticationMigration.ReusableGmailAccountIds,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            await SaveCheckpointAsync(destination, activeStep.Value, accountOptions).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                activeStep = MigrationStepKind.ConfigureFeatures;
                metadata = await ReadMetadataAsync(destination).ConfigureAwait(false);
                if (metadata.LastCompletedStep < (int)activeStep.Value)
                {
                    await RunVisibleStepAsync(
                        activeStep.Value,
                        "Setting up selected features",
                        "Wino is applying calendar, contacts, To Do and mail-filter choices without copying legacy contacts.",
                        async () =>
                        {
                            try
                            {
                                await MigrateProfilePicturesAsync(destination, cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException($"Migrating account pictures failed: {ex.Message}", ex);
                            }

                            try
                            {
                                await SaveCheckpointAsync(destination, activeStep.Value, accountOptions).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                throw new InvalidOperationException($"Saving the migration checkpoint failed: {ex.Message}", ex);
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                await DetachLegacyAsync(destination).ConfigureAwait(false);
                await destination.CloseAsync().ConfigureAwait(false);
                destination = null;

                activeStep = MigrationStepKind.ValidateAndFinalize;
                await RunVisibleStepAsync(
                    activeStep.Value,
                    "Verifying your data",
                    "Wino is checking the new database before it becomes active.",
                    async () =>
                    {
                        await ValidateCopiedDataAsync(plan, cancellationToken).ConfigureAwait(false);
                        await MarkMigrationCompletedAsync(plan.StagingPath, accountOptions, cancellationToken)
                            .ConfigureAwait(false);

                        var finalValidation = await _schemaService.ValidateAsync(
                                plan.StagingPath,
                                cancellationToken,
                                requireCompletedMigration: true)
                            .ConfigureAwait(false);
                        if (!finalValidation.IsValid)
                            throw new InvalidDataException(finalValidation.ErrorMessage ?? finalValidation.IntegrityResult);

                        await _authenticationTokenMigrationService
                            .FinalizeAsync(accountOptions, cancellationToken)
                            .ConfigureAwait(false);

                        PromoteStagingDatabase(plan.StagingPath, plan.DestinationPath);
                    },
                    cancellationToken).ConfigureAwait(false);

                var resultConnection = new SQLiteAsyncConnection(
                    plan.DestinationPath,
                    SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
                var accountCount = await CountAsync(resultConnection, "MailAccount").ConfigureAwait(false);
                var mailCount = await CountAsync(resultConnection, "MailCopy").ConfigureAwait(false);
                var calendarCount = await CountAsync(resultConnection, "CalendarItem").ConfigureAwait(false);
                await resultConnection.CloseAsync().ConfigureAwait(false);

                activeStep = MigrationStepKind.Completed;
                await RunVisibleStepAsync(
                    activeStep.Value,
                    "Migration complete",
                    "The version 210 database is ready to launch.",
                    () => Task.CompletedTask,
                    cancellationToken).ConfigureAwait(false);

                var pendingAuthorization = accountOptions
                    .Where(RequiresFeatureAuthorization)
                    .ToArray();
                return new MigrationResult(
                    pendingAuthorization.Length > 0 ? MigrationStatus.AwaitingUser : MigrationStatus.Completed,
                    AccountCount: (int)accountCount,
                    MailCount: mailCount,
                    CalendarCount: calendarCount,
                    MigratedRowCounts: rowCounts);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (activeStep.HasValue)
                {
                    Report(activeStep.Value, MigrationStepStatus.Failed,
                        "We couldn't complete this step",
                        "Your old Wino data was not changed. Try again or start fresh.",
                        0,
                        SanitizeError(ex));
                }

                return new MigrationResult(MigrationStatus.Failed, activeStep, SanitizeError(ex));
            }
            finally
            {
                if (destination != null)
                {
                    try
                    {
                        await DetachLegacyAsync(destination).ConfigureAwait(false);
                        await destination.CloseAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // The staging database remains available for the next retry.
                    }
                }
            }
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    public async Task<MigrationResult> StartFreshAsync(CancellationToken cancellationToken = default)
    {
        await _migrationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stagingPath = GetPath(StagingDatabaseName);
            var destinationPath = GetPath(DatabaseService.CurrentDatabaseName);
            DeleteGeneratedDatabase(stagingPath);
            DeleteGeneratedDatabase(destinationPath);

            var connection = await _schemaService.CreateAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            await CreateMetadataTableAsync(connection).ConfigureAwait(false);
            await UpsertMetadataAsync(connection, new MigrationMetadataRow
            {
                Id = MetadataRowId,
                SourcePath = GetPath(DatabaseService.LegacyDatabaseName),
                LastCompletedStep = (int)MigrationStepKind.Completed,
                Status = MigrationStatus.Skipped,
                UpdatedAtUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
            await connection.CloseAsync().ConfigureAwait(false);

            var validation = await _schemaService.ValidateAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.ErrorMessage ?? validation.IntegrityResult);

            return new MigrationResult(MigrationStatus.Skipped);
        }
        catch (Exception ex)
        {
            return new MigrationResult(MigrationStatus.Failed, ErrorMessage: SanitizeError(ex));
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    public async Task MarkAccountAuthorizationResolvedAsync(
        Guid accountId,
        bool wasSkipped,
        CancellationToken cancellationToken = default)
    {
        var destinationPath = GetPath(DatabaseService.CurrentDatabaseName);
        if (!File.Exists(destinationPath))
            throw new FileNotFoundException("The migrated database could not be found.", destinationPath);

        var connection = new SQLiteAsyncConnection(destinationPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await ReadMetadataAsync(connection).ConfigureAwait(false);
            var pendingIds = ParseAccountIds(metadata.PendingAuthenticationAccountIds);
            pendingIds.Remove(accountId);
            metadata.PendingAuthenticationAccountIds = string.Join(",", pendingIds.Select(id => id.ToString("N")));
            var deferredIds = ParseAccountIds(metadata.DeferredAccountIds);
            if (wasSkipped)
                deferredIds.Add(accountId);
            else
                deferredIds.Remove(accountId);
            metadata.DeferredAccountIds = string.Join(",", deferredIds.Select(id => id.ToString("N")));
            metadata.IsAuthenticationQueueInitialized = true;
            metadata.UpdatedAtUtc = DateTime.UtcNow;
            await UpsertMetadataAsync(connection, metadata).ConfigureAwait(false);
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task CheckSourceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The old Wino database could not be found.", sourcePath);

        var sourceLength = new FileInfo(sourcePath).Length;
        var root = Path.GetPathRoot(sourcePath);
        if (!string.IsNullOrWhiteSpace(root))
        {
            var requiredBytes = Math.Max(256L * 1024 * 1024, (long)Math.Ceiling(sourceLength * 1.5));
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace < requiredBytes)
                throw new IOException($"Free at least {FormatBytes(requiredBytes - drive.AvailableFreeSpace)} and try again.");
        }

        var source = new SQLiteAsyncConnection(
            sourcePath,
            SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var integrity = await source.ExecuteScalarAsync<string>("PRAGMA integrity_check;").ConfigureAwait(false);
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The old Wino database is damaged and could not be verified.");

            var accountColumns = await source.GetTableInfoAsync("MailAccount").ConfigureAwait(false);
            if (accountColumns.Count == 0)
                throw new InvalidDataException("The old Wino database does not contain account definitions.");
        }
        finally
        {
            await source.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task<SQLiteAsyncConnection> OpenOrCreateStagingAsync(
        MigrationPlan plan,
        IReadOnlyList<MigrationAccountOptions> accountOptions,
        CancellationToken cancellationToken)
    {
        if (File.Exists(plan.StagingPath))
        {
            try
            {
                var existing = new SQLiteAsyncConnection(plan.StagingPath);
                var metadata = await ReadMetadataAsync(existing).ConfigureAwait(false);
                if (metadata != null && string.Equals(metadata.SourcePath, plan.SourcePath, StringComparison.OrdinalIgnoreCase))
                    return existing;

                await existing.CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // A stale or invalid staging file is replaced below. The legacy source remains untouched.
            }

            DeleteGeneratedDatabase(plan.StagingPath);
        }

        var connection = await _schemaService.CreateAsync(plan.StagingPath, cancellationToken).ConfigureAwait(false);
        await CreateMetadataTableAsync(connection).ConfigureAwait(false);
        await UpsertMetadataAsync(connection, new MigrationMetadataRow
        {
            Id = MetadataRowId,
            SourcePath = plan.SourcePath,
            LastCompletedStep = (int)MigrationStepKind.PrepareDatabase,
            Status = MigrationStatus.Running,
            OptionsJson = BuildOptionsSummary(accountOptions),
            UpdatedAtUtc = DateTime.UtcNow
        }).ConfigureAwait(false);
        return connection;
    }

    private async Task CopyTablesAsync(
        SQLiteAsyncConnection destination,
        IReadOnlyList<string> tables,
        IDictionary<string, long> rowCounts,
        MigrationStepKind step,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < tables.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = tables[index];
            var sourceColumns = await destination.QueryAsync<TableColumnRow>(
                $"PRAGMA legacy.table_info({QuoteLiteral(table)});").ConfigureAwait(false);
            if (sourceColumns.Count == 0)
                continue;

            var destinationColumns = await destination.QueryAsync<TableColumnRow>(
                $"PRAGMA main.table_info({QuoteLiteral(table)});").ConfigureAwait(false);
            if (destinationColumns.Count == 0)
                continue;

            var destinationColumnNames = destinationColumns.Select(column => column.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var commonColumns = sourceColumns.Select(column => column.Name)
                .Where(destinationColumnNames.Contains)
                .ToList();
            if (commonColumns.Count == 0)
                continue;

            var columnList = string.Join(", ", commonColumns.Select(QuoteIdentifier));
            await destination.RunInTransactionAsync(connection =>
            {
                connection.Execute($"DELETE FROM main.{QuoteIdentifier(table)};");
                connection.Execute(
                    $"INSERT INTO main.{QuoteIdentifier(table)} ({columnList}) " +
                    $"SELECT {columnList} FROM legacy.{QuoteIdentifier(table)};");
            }).ConfigureAwait(false);

            var count = await CountAsync(destination, table).ConfigureAwait(false);
            rowCounts[table] = count;
            Report(step, MigrationStepStatus.Running, string.Empty, string.Empty,
                (index + 1d) / tables.Count, $"{table}: {count:N0}");
        }
    }

    private async Task ApplyAccountMappingsAsync(
        SQLiteAsyncConnection connection,
        IReadOnlyList<MigrationAccountOptions> accountOptions,
        IReadOnlyCollection<Guid> reusableGmailAccountIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.ExecuteAsync($@"
UPDATE MailAccount
SET IsCalendarAccessEnabled = IsCalendarAccessGranted,
    CalendarIntegrationSource = CASE
        WHEN IsCalendarAccessGranted = 1 AND ProviderType IN ({(int)MailProviderType.Outlook}, {(int)MailProviderType.Gmail}) THEN {(int)AccountIntegrationSource.Provider}
        WHEN IsCalendarAccessGranted = 1 AND ProviderType = {(int)MailProviderType.IMAP4}
             AND EXISTS (SELECT 1 FROM CustomServerInformation c WHERE c.AccountId = MailAccount.Id AND COALESCE(c.CalDavServiceUrl, '') <> '') THEN {(int)AccountIntegrationSource.Dav}
        ELSE {(int)AccountIntegrationSource.Local}
    END,
    IsContactAccessEnabled = 0,
    IsContactAccessGranted = 0,
    IsContactReauthorizationRequired = 0,
    ContactIntegrationSource = CASE WHEN ProviderType IN ({(int)MailProviderType.Outlook}, {(int)MailProviderType.Gmail}) THEN {(int)AccountIntegrationSource.Provider} ELSE {(int)AccountIntegrationSource.Local} END,
    IsTaskAccessEnabled = 0,
    IsTaskAccessGranted = 0,
    IsTaskReauthorizationRequired = 0,
    TaskIntegrationSource = CASE WHEN ProviderType IN ({(int)MailProviderType.Outlook}, {(int)MailProviderType.Gmail}) THEN {(int)AccountIntegrationSource.Provider} ELSE {(int)AccountIntegrationSource.Local} END,
    IsProtocolLogEnabled = 0;").ConfigureAwait(false);

            await connection.ExecuteAsync(@"
UPDATE MailAccountPreferences
SET IsSemanticIndexingEnabled = 0,
    AutomaticallyIndexNewMessages = 0;").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Applying account capability defaults failed: {ex.Message}", ex);
        }

        try
        {
            await connection.ExecuteAsync(
                $"UPDATE CustomServerInformation SET ConnectionPolicyVersion = {(int)ImapConnectionPolicyVersion.Legacy};")
                .ConfigureAwait(false);

            await connection.ExecuteAsync("DELETE FROM AccountProviderFeature;").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Preparing optional feature storage failed: {ex.Message}", ex);
        }

        foreach (var option in accountOptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var providerBacked = option.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook;
            var contactsEnabled = providerBacked && option.EnableContacts;
            var providerTasksEnabled = providerBacked && option.EnableTasks;
            var localTasksEnabled = option.ProviderType == MailProviderType.IMAP4 && option.EnableTasks;
            var hasReusableGmailToken = reusableGmailAccountIds.Contains(option.AccountId);

            try
            {
                await connection.ExecuteAsync($@"
UPDATE MailAccount
SET IsContactAccessEnabled = ?,
    IsContactReauthorizationRequired = ?,
    IsTaskAccessEnabled = ?,
    IsTaskReauthorizationRequired = ?,
    AttentionReason = CASE
        WHEN ProviderType = {(int)MailProviderType.Gmail} AND ? = 1 AND AttentionReason = {(int)AccountAttentionReason.InvalidCredentials} THEN {(int)AccountAttentionReason.None}
        WHEN ProviderType = {(int)MailProviderType.Gmail} AND ? = 1 AND ? = 0 THEN {(int)AccountAttentionReason.InvalidCredentials}
        ELSE AttentionReason
    END
WHERE Id = ?;",
                contactsEnabled,
                contactsEnabled && option.DeferSignIn,
                providerTasksEnabled || localTasksEnabled,
                providerTasksEnabled && option.DeferSignIn,
                hasReusableGmailToken,
                option.DeferSignIn,
                hasReusableGmailToken,
                    option.AccountId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Applying choices for account {option.AccountId} failed: {ex.Message}", ex);
            }

            if (providerBacked && option.EnableMailFilters)
            {
                try
                {
                    await connection.InsertAsync(new AccountProviderFeature
                    {
                        Id = Guid.NewGuid(),
                        MailAccountId = option.AccountId,
                        Feature = ProviderFeature.MailFilters,
                        AuthorizationState = ProviderFeatureAuthorizationState.ReauthorizationRequired,
                        EnabledAtUtc = DateTime.UtcNow
                    }, typeof(AccountProviderFeature)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Preparing mail filters for account {option.AccountId} failed: {ex.Message}", ex);
                }
            }
        }
    }

    private async Task MigrateProfilePicturesAsync(SQLiteAsyncConnection connection, CancellationToken cancellationToken)
    {
        var accounts = await connection.Table<MailAccount>()
            .Where(account => account.Base64ProfilePictureData != null && account.Base64ProfilePictureData != string.Empty)
            .ToListAsync().ConfigureAwait(false);

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!account.ProfilePictureFileId.HasValue)
                {
                    account.ProfilePictureFileId = await _profilePictureFileService
                        .SaveProfilePictureAsync(
                            Convert.FromBase64String(account.Base64ProfilePictureData),
                            account.Id,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                account.Base64ProfilePictureData = string.Empty;
                account.IsProfilePictureBackfillComplete = account.ProfilePictureFileId.HasValue;
                await connection.UpdateAsync(account, typeof(MailAccount)).ConfigureAwait(false);
            }
            catch (FormatException)
            {
                account.Base64ProfilePictureData = string.Empty;
                await connection.UpdateAsync(account, typeof(MailAccount)).ConfigureAwait(false);
            }
        }
    }

    private async Task ValidateCopiedDataAsync(MigrationPlan plan, CancellationToken cancellationToken)
    {
        var validation = await _schemaService.ValidateAsync(plan.StagingPath, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
            throw new InvalidDataException(validation.ErrorMessage ?? $"Database integrity check returned '{validation.IntegrityResult}'.");

        var source = new SQLiteAsyncConnection(plan.SourcePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        var destination = new SQLiteAsyncConnection(plan.StagingPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        try
        {
            foreach (var table in AccountTables.Concat(MailTables).Concat(CalendarTables))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceInfo = await source.GetTableInfoAsync(table).ConfigureAwait(false);
                if (sourceInfo.Count == 0)
                    continue;

                var sourceCount = await CountAsync(source, table).ConfigureAwait(false);
                var destinationCount = await CountAsync(destination, table).ConfigureAwait(false);
                if (sourceCount != destinationCount)
                    throw new InvalidDataException($"The {table} row count did not match after migration.");
            }

            var sourceAccountIds = await source.QueryScalarsAsync<Guid>("SELECT Id FROM MailAccount ORDER BY Id;")
                .ConfigureAwait(false);
            var destinationAccountIds = await destination.QueryScalarsAsync<Guid>("SELECT Id FROM MailAccount ORDER BY Id;")
                .ConfigureAwait(false);
            if (!sourceAccountIds.SequenceEqual(destinationAccountIds))
                throw new InvalidDataException("Account identifiers did not match after migration.");
        }
        finally
        {
            await source.CloseAsync().ConfigureAwait(false);
            await destination.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task<List<LegacyAccountRow>> ReadLegacyAccountsAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var connection = new SQLiteAsyncConnection(sourcePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await connection.QueryAsync<LegacyAccountRow>(
                "SELECT Id, Name, Address, ProviderType, \"Order\" FROM MailAccount;").ConfigureAwait(false);
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static Task AttachLegacyAsync(SQLiteAsyncConnection destination, string sourcePath)
        => destination.ExecuteAsync("ATTACH DATABASE ? AS legacy;", Path.GetFullPath(sourcePath));

    private static async Task DetachLegacyAsync(SQLiteAsyncConnection destination)
    {
        try
        {
            await destination.ExecuteAsync("DETACH DATABASE legacy;").ConfigureAwait(false);
        }
        catch
        {
            // The source is not attached when a failure occurred before the copy phase.
        }
    }

    private static async Task CreateMetadataTableAsync(SQLiteAsyncConnection connection)
    {
        await connection.CreateTableAsync<MigrationMetadataRow>().ConfigureAwait(false);

        var columns = await connection.GetTableInfoAsync(MetadataTableName).ConfigureAwait(false);
        if (!columns.Any(column => column.Name == nameof(MigrationMetadataRow.PendingAuthenticationAccountIds)))
        {
            await connection.ExecuteAsync(
                $"ALTER TABLE {MetadataTableName} ADD COLUMN {nameof(MigrationMetadataRow.PendingAuthenticationAccountIds)} TEXT;")
                .ConfigureAwait(false);
        }

        if (!columns.Any(column => column.Name == nameof(MigrationMetadataRow.IsAuthenticationQueueInitialized)))
        {
            await connection.ExecuteAsync(
                $"ALTER TABLE {MetadataTableName} ADD COLUMN {nameof(MigrationMetadataRow.IsAuthenticationQueueInitialized)} INTEGER NOT NULL DEFAULT 0;")
                .ConfigureAwait(false);
        }
    }

    private static async Task<MigrationMetadataRow> ReadMetadataAsync(SQLiteAsyncConnection connection)
    {
        await CreateMetadataTableAsync(connection).ConfigureAwait(false);
        return await connection.FindAsync<MigrationMetadataRow>(MetadataRowId).ConfigureAwait(false)
               ?? new MigrationMetadataRow();
    }

    private static Task UpsertMetadataAsync(SQLiteAsyncConnection connection, MigrationMetadataRow metadata)
        => connection.InsertOrReplaceAsync(metadata, typeof(MigrationMetadataRow));

    private static async Task MarkMigrationCompletedAsync(
        string stagingPath,
        IReadOnlyList<MigrationAccountOptions> accountOptions,
        CancellationToken cancellationToken)
    {
        var connection = new SQLiteAsyncConnection(stagingPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await ReadMetadataAsync(connection).ConfigureAwait(false);
            var tableCounts = new List<string>();
            foreach (var table in AccountTables.Concat(MailTables).Concat(CalendarTables).Distinct())
            {
                var info = await connection.GetTableInfoAsync(table).ConfigureAwait(false);
                if (info.Count > 0)
                    tableCounts.Add($"{table}:{await CountAsync(connection, table).ConfigureAwait(false)}");
            }

            metadata.LastCompletedStep = (int)MigrationStepKind.Completed;
            metadata.Status = MigrationStatus.Completed;
            metadata.OptionsJson = BuildOptionsSummary(accountOptions);
            metadata.RowCounts = string.Join(";", tableCounts);
            metadata.DeferredAccountIds = string.Join(",", accountOptions
                .Where(option => option.DeferSignIn)
                .Select(option => option.AccountId.ToString("N")));
            metadata.PendingAuthenticationAccountIds = string.Join(",", accountOptions
                .Where(RequiresFeatureAuthorization)
                .Select(option => option.AccountId.ToString("N")));
            metadata.IsAuthenticationQueueInitialized = true;
            metadata.UpdatedAtUtc = DateTime.UtcNow;
            await UpsertMetadataAsync(connection, metadata).ConfigureAwait(false);
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task SaveCheckpointAsync(
        SQLiteAsyncConnection connection,
        MigrationStepKind step,
        IReadOnlyList<MigrationAccountOptions> accountOptions)
    {
        var metadata = await ReadMetadataAsync(connection).ConfigureAwait(false);
        metadata.LastCompletedStep = (int)step;
        metadata.Status = MigrationStatus.Running;
        metadata.OptionsJson = BuildOptionsSummary(accountOptions);
        metadata.UpdatedAtUtc = DateTime.UtcNow;
        await UpsertMetadataAsync(connection, metadata).ConfigureAwait(false);
    }

    private async Task RunVisibleStepAsync(
        MigrationStepKind step,
        string title,
        string description,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        await RunVisibleStepAsync<object>(step, title, description, async () =>
        {
            await action().ConfigureAwait(false);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunVisibleStepAsync<T>(
        MigrationStepKind step,
        string title,
        string description,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        Report(step, MigrationStepStatus.Running, title, description, 0);
        var startedAt = _clock.UtcNow;
        var result = await action().ConfigureAwait(false);
        var remaining = MinimumStepVisibility - (_clock.UtcNow - startedAt);
        if (remaining > TimeSpan.Zero)
            await _clock.DelayAsync(remaining, cancellationToken).ConfigureAwait(false);

        Report(step, MigrationStepStatus.Completed, title, description, 1);
        return result;
    }

    private void Report(
        MigrationStepKind step,
        MigrationStepStatus status,
        string title,
        string description,
        double progress,
        string detail = null)
        => ProgressChanged?.Invoke(this, new MigrationProgress(step, status, title, description, progress, detail));

    private string GetPath(string fileName) => Path.Combine(_configuration.PublisherSharedFolderPath, fileName);

    private static async Task<long> CountAsync(SQLiteAsyncConnection connection, string table)
        => await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {QuoteIdentifier(table)};")
            .ConfigureAwait(false);

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private string SanitizeError(Exception exception)
    {
        var sanitizedMessage = exception.Message
            .Replace(_configuration.PublisherSharedFolderPath, "[Wino data]", StringComparison.OrdinalIgnoreCase)
            .Replace(Environment.NewLine, " ", StringComparison.Ordinal);

        return exception switch
        {
            IOException => sanitizedMessage,
            InvalidDataException => sanitizedMessage,
            UnauthorizedAccessException => "Wino could not access the database files. Check file permissions and try again.",
            _ => $"{exception.GetType().Name}: {sanitizedMessage}"
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        return $"{bytes / (1024d * 1024):0} MB";
    }

    private static string BuildOptionsSummary(IReadOnlyList<MigrationAccountOptions> accountOptions)
        => string.Join(
            ";",
            accountOptions.Select(option =>
                $"{option.AccountId:N}:{Convert.ToInt32(option.EnableContacts)}:{Convert.ToInt32(option.EnableTasks)}:" +
                $"{Convert.ToInt32(option.EnableMailFilters)}:{Convert.ToInt32(option.DeferSignIn)}"));

    private async Task<IReadOnlyList<MigrationAccountOptions>> ReadPendingAuthorizationAccountsAsync(
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var connection = new SQLiteAsyncConnection(destinationPath);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await ReadMetadataAsync(connection).ConfigureAwait(false);
            if (!metadata.IsAuthenticationQueueInitialized)
            {
                var derivedIds = await DerivePendingAuthorizationAccountIdsAsync(connection).ConfigureAwait(false);
                metadata.PendingAuthenticationAccountIds = string.Join(",", derivedIds.Select(id => id.ToString("N")));
                metadata.IsAuthenticationQueueInitialized = true;
                metadata.UpdatedAtUtc = DateTime.UtcNow;
                await UpsertMetadataAsync(connection, metadata).ConfigureAwait(false);
            }

            var pendingIds = ParseAccountIds(metadata.PendingAuthenticationAccountIds);
            if (pendingIds.Count == 0)
                return [];

            var accounts = await connection.Table<MailAccount>().ToListAsync().ConfigureAwait(false);
            var filterFeatures = await connection.Table<AccountProviderFeature>()
                .Where(feature => feature.Feature == ProviderFeature.MailFilters)
                .ToListAsync().ConfigureAwait(false);
            var filterAccountIds = filterFeatures.Select(feature => feature.MailAccountId).ToHashSet();

            return accounts
                .Where(account => pendingIds.Contains(account.Id))
                .OrderBy(account => account.Order)
                .Select(account => new MigrationAccountOptions(
                    account.Id,
                    string.IsNullOrWhiteSpace(account.Name) ? account.Address : account.Name,
                    account.Address,
                    account.ProviderType,
                    account.IsContactAccessEnabled,
                    account.IsTaskAccessEnabled,
                    filterAccountIds.Contains(account.Id),
                    DeferSignIn: true))
                .ToArray();
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<Guid>> DerivePendingAuthorizationAccountIdsAsync(
        SQLiteAsyncConnection connection)
    {
        var accounts = await connection.Table<MailAccount>().ToListAsync().ConfigureAwait(false);
        var pendingIds = accounts
            .Where(account => account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook)
            .Where(account =>
                account.IsContactAccessEnabled && account.IsContactReauthorizationRequired ||
                account.IsTaskAccessEnabled && account.IsTaskReauthorizationRequired)
            .Select(account => account.Id)
            .ToHashSet();
        var features = await connection.Table<AccountProviderFeature>()
            .Where(feature => feature.AuthorizationState == ProviderFeatureAuthorizationState.ReauthorizationRequired)
            .ToListAsync().ConfigureAwait(false);
        pendingIds.UnionWith(features.Select(feature => feature.MailAccountId));
        return pendingIds;
    }

    private static HashSet<Guid> ParseAccountIds(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var accountId) ? accountId : Guid.Empty)
            .Where(accountId => accountId != Guid.Empty)
            .ToHashSet();
    }

    private static bool RequiresFeatureAuthorization(MigrationAccountOptions option)
        => option.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook &&
           (option.EnableContacts || option.EnableTasks || option.EnableMailFilters);

    private static void PromoteStagingDatabase(string stagingPath, string destinationPath)
    {
        File.Move(stagingPath, destinationPath, overwrite: true);

        foreach (var sidecar in new[] { destinationPath + "-wal", destinationPath + "-shm" })
        {
            if (File.Exists(sidecar))
                File.Delete(sidecar);
        }
    }

    private static void DeleteGeneratedDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Table(MetadataTableName)]
    public sealed class MigrationMetadataRow
    {
        [PrimaryKey]
        public int Id { get; set; }
        public string SourcePath { get; set; }
        public int LastCompletedStep { get; set; }
        public MigrationStatus Status { get; set; }
        public string OptionsJson { get; set; }
        public string RowCounts { get; set; }
        public string DeferredAccountIds { get; set; }
        public string PendingAuthenticationAccountIds { get; set; }
        public bool IsAuthenticationQueueInitialized { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public sealed class TableColumnRow
    {
        [Column("name")]
        public string Name { get; set; }
    }

    public sealed class LegacyAccountRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public MailProviderType ProviderType { get; set; }
        public int Order { get; set; }
    }
}

public sealed class SystemMigrationClock : IMigrationClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);
}
