using System.IO;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Authentication;
using MailCal.Core.Domain.Models.Migration;

namespace MailCal.Services;

/// <summary>
/// Moves local data that builds from before the Mail-Cal rename stored under "Wino" names.
/// Runs on every launch before any service opens the database, and only moves what exists
/// under the old name and is missing under the new one.
/// </summary>
public static class LegacyNameMigration
{
    private const string LegacyDatabaseName = "Wino210.db";
    private const string LegacyGmailTokenStoreFolderName = "WinoMailGmailTokenStore";

    // The main file moves last, so a move interrupted halfway is completed on the next launch.
    private static readonly string[] DatabaseFileSuffixes = ["-wal", "-shm", "-journal", ""];

    public static void Run(IApplicationConfiguration configuration)
    {
        var root = MainDatabasePaths.GetRoot(configuration);

        MoveDatabase(Path.Combine(root, LegacyDatabaseName), Path.Combine(root, DatabaseService.CurrentDatabaseName));
        MoveDatabase(Path.Combine(root, LegacyDatabaseName + ".migrating"), Path.Combine(root, DatabaseService.CurrentDatabaseName + ".migrating"));

        var legacyTokenStore = Path.Combine(root, LegacyGmailTokenStoreFolderName);
        var tokenStore = AuthenticationTokenStorePaths.GetGmailTokenStorePath(configuration);
        if (Directory.Exists(legacyTokenStore) && !Directory.Exists(tokenStore))
            Directory.Move(legacyTokenStore, tokenStore);
    }

    private static void MoveDatabase(string legacyPath, string currentPath)
    {
        if (!File.Exists(legacyPath) || File.Exists(currentPath))
            return;

        // Without a current main file, any current sidecar is stale and must not pair with the moved database.
        foreach (var suffix in DatabaseFileSuffixes)
        {
            if (File.Exists(legacyPath + suffix))
                File.Move(legacyPath + suffix, currentPath + suffix, overwrite: suffix.Length > 0);
        }
    }
}
