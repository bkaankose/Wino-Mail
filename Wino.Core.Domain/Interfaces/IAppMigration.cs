using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Represents a one-time app or data migration that runs when a user updates to a new version.
/// </summary>
public interface IAppMigration
{
    /// <summary>Gets the unique identifier for this migration, used to track completion in local settings.</summary>
    string MigrationId { get; }

    /// <summary>Executes the migration logic.</summary>
    Task ExecuteAsync();
}
