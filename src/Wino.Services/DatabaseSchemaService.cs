using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed record DatabaseValidationResult(
    bool IsValid,
    int SchemaVersion,
    string IntegrityResult,
    IReadOnlyList<string> ForeignKeyFailures,
    string ErrorMessage = null);

public interface IDatabaseSchemaService
{
    Task<SQLiteAsyncConnection> CreateAsync(string databasePath, CancellationToken cancellationToken = default);
    Task<DatabaseValidationResult> ValidateAsync(
        string databasePath,
        CancellationToken cancellationToken = default,
        bool requireCompletedMigration = false);
}

public sealed class DatabaseSchemaService(IApplicationConfiguration applicationConfiguration) : IDatabaseSchemaService
{
    public async Task<SQLiteAsyncConnection> CreateAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(databasePath);
        var configuredRoot = Path.GetFullPath(applicationConfiguration.PublisherSharedFolderPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), configuredRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The migration database must be created in the publisher cache folder.");

        var databaseService = new DatabaseService(applicationConfiguration, Path.GetFileName(fullPath));
        await databaseService.InitializeAsync().ConfigureAwait(false);
        return databaseService.Connection;
    }

    public async Task<DatabaseValidationResult> ValidateAsync(
        string databasePath,
        CancellationToken cancellationToken = default,
        bool requireCompletedMigration = false)
    {
        if (string.IsNullOrWhiteSpace(databasePath) || !File.Exists(databasePath))
            return new DatabaseValidationResult(false, 0, string.Empty, [], "The database file does not exist.");

        SQLiteAsyncConnection connection = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            connection = new SQLiteAsyncConnection(
                databasePath,
                SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);

            var integrity = await connection.ExecuteScalarAsync<string>("PRAGMA integrity_check;")
                .ConfigureAwait(false);
            var schemaVersion = await connection.ExecuteScalarAsync<int>("PRAGMA user_version;")
                .ConfigureAwait(false);
            var foreignKeyRows = await connection.QueryAsync<ForeignKeyFailureRow>("PRAGMA foreign_key_check;")
                .ConfigureAwait(false);
            var failures = new List<string>(foreignKeyRows.Count);
            foreach (var row in foreignKeyRows)
                failures.Add($"{row.Table}:{row.RowId}->{row.Parent}");

            var completed = true;
            if (requireCompletedMigration)
            {
                var metadataExists = await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__MigrationMetadata';")
                    .ConfigureAwait(false);
                completed = metadataExists == 1 && await connection.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM __MigrationMetadata WHERE Id = 1 AND Status IN (?, ?);",
                    (int)Wino.Core.Domain.Models.Migration.MigrationStatus.Completed,
                    (int)Wino.Core.Domain.Models.Migration.MigrationStatus.Skipped).ConfigureAwait(false) == 1;
            }

            return new DatabaseValidationResult(
                string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) &&
                schemaVersion == DatabaseService.CurrentSchemaVersion &&
                failures.Count == 0 && completed,
                schemaVersion,
                integrity,
                failures,
                completed ? null : "The database migration is not marked complete.");
        }
        catch (Exception ex)
        {
            return new DatabaseValidationResult(false, 0, string.Empty, [], ex.Message);
        }
        finally
        {
            if (connection != null)
                await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    private sealed class ForeignKeyFailureRow
    {
        [Column("table")]
        public string Table { get; set; }

        [Column("rowid")]
        public long RowId { get; set; }

        [Column("parent")]
        public string Parent { get; set; }
    }
}
