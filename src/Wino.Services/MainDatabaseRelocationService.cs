using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Migration;

namespace Wino.Services;

/// <summary>Creates a consistent, private migration input without changing publisher data.</summary>
public sealed class MainDatabaseRelocationService(
    IApplicationConfiguration configuration,
    IDatabaseSchemaService schema,
    Func<string, long> availableFreeSpace = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = MainDatabasePaths.GetPath(configuration, DatabaseService.CurrentDatabaseName);
            var legacy = MainDatabasePaths.GetPath(configuration, DatabaseService.LegacyDatabaseName);
            // Existing local work always wins, including interrupted schema migrations.
            if (File.Exists(current) || File.Exists(legacy) || File.Exists(current + ".migrating") ||
                !configuration.AllowLegacyDataMigration)
                return;

            ArgumentException.ThrowIfNullOrWhiteSpace(configuration.PublisherSharedFolderPath);
            var publisherCurrent = Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.CurrentDatabaseName);
            var publisherLegacy = Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.LegacyDatabaseName);
            var completed = File.Exists(publisherCurrent);
            var source = completed ? publisherCurrent : publisherLegacy;
            if (!File.Exists(source))
                return;

            var destination = completed ? current : legacy;
            Directory.CreateDirectory(MainDatabasePaths.GetRoot(configuration));
            var temporary = destination + ".copying";
            // A failed snapshot is never a migration input. The publisher source is retained.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                if (File.Exists(temporary + suffix))
                    File.Delete(temporary + suffix);
            }

            var sourceSize = new FileInfo(source).Length;
            if (File.Exists(source + "-wal"))
                sourceSize += new FileInfo(source + "-wal").Length;

            var freeSpace = availableFreeSpace?.Invoke(destination) ?? new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
            if (freeSpace < checked(sourceSize * 2 + 16 * 1024 * 1024))
                throw new IOException("There is not enough space to copy and migrate the Wino database.");

            var connection = new SQLiteAsyncConnection(source, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await connection.BackupAsync(temporary).ConfigureAwait(false);
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (completed)
            {
                var validation = await schema.ValidateAsync(temporary, cancellationToken, requireCompletedMigration: true).ConfigureAwait(false);
                if (!validation.IsValid)
                    throw new InvalidDataException(validation.ErrorMessage ?? validation.IntegrityResult);
            }

            var snapshot = new SQLiteAsyncConnection(temporary, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
            try
            {
                if (await snapshot.ExecuteScalarAsync<string>("PRAGMA integrity_check;").ConfigureAwait(false) != "ok")
                    throw new InvalidDataException("The copied database failed its integrity check.");

                if (completed)
                {
                    var accounts = await snapshot.Table<MailAccount>().ToListAsync().ConfigureAwait(false);
                    await new AuthenticationTokenMigrationService(configuration).PrepareAsync(
                        accounts.Select(account => new MigrationAccountOptions(account.Id, account.Name, account.Address,
                            account.ProviderType, false, false, false)).ToArray(), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await snapshot.CloseAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
