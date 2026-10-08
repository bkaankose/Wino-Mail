namespace Wino.Core.Domain.Enums;

/// <summary>
/// Indicates the source of an entity update.
/// </summary>
public enum EntityUpdateSource
{
    /// <summary>
    /// Update originated from client-side optimistic UI changes (ApplyUIChanges).
    /// </summary>
    ClientUpdated,

    /// <summary>
    /// Update originated from reverting client-side optimistic UI changes (RevertUIChanges).
    /// </summary>
    ClientReverted,

    /// <summary>
    /// Update originated from server synchronization or database operations.
    /// </summary>
    Server
}
