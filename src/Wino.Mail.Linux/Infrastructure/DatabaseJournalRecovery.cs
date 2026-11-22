using System;
using System.IO;
using SQLite;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Migration;
using Wino.Services;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Rolls back a hot journal before the shared startup reads the database. A write interrupted by a crash leaves
/// Wino210.db-journal behind, and the shared lifecycle check opens the file read-only, which cannot roll it back:
/// every later start then fails with "attempt to write a readonly database".
/// </summary>
internal static class DatabaseJournalRecovery
{
    public static void Recover(IApplicationConfiguration configuration)
    {
        string path;
        try { path = MainDatabasePaths.GetPath(configuration, DatabaseService.CurrentDatabaseName); }
        catch (ArgumentException) { return; }
        if (!File.Exists(path) || !File.Exists(path + "-journal")) return;

        try
        {
            // Flags that differ from the main connection's keep this handle out of sqlite-net's shared pool entry.
            using var connection = new SQLiteConnection(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex | SQLiteOpenFlags.PrivateCache);
            connection.BusyTimeout = TimeSpan.FromSeconds(1);
            connection.ExecuteScalar<int>("PRAGMA user_version;");
            Serilog.Log.Information("Database journal recovery ran; journal left: {Exists}.", File.Exists(path + "-journal"));
        }
        catch (Exception exception) when (exception is SQLiteException or IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(exception, "The database journal could not be rolled back.");
        }
    }
}
