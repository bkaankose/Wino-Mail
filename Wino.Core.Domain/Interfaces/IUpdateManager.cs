using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Updates;

namespace Wino.Core.Domain.Interfaces;

public interface IUpdateManager
{
    /// <summary>Loads and parses the update notes for the current version from the bundled asset file.</summary>
    Task<UpdateNotes> GetLatestUpdateNotesAsync();

    /// <summary>Returns true if the current version's update notes have not yet been shown to the user.</summary>
    bool ShouldShowUpdateNotes();

    /// <summary>Stores a flag in local settings indicating the update notes for the current version have been seen.</summary>
    void MarkUpdateNotesAsSeen();

    /// <summary>Returns true if any registered migration has not yet been completed.</summary>
    bool HasPendingMigrations();

    /// <summary>Runs all pending migrations in order and marks each as completed in local settings.</summary>
    Task RunPendingMigrationsAsync();

    /// <summary>Registers migrations to be tracked and executed by this manager.</summary>
    void RegisterMigrations(IEnumerable<IAppMigration> migrations);
}
