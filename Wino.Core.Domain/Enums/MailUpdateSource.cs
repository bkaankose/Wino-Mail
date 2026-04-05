namespace Wino.Core.Domain.Enums;

/// <summary>
/// Indicates the source of a mail update.
/// </summary>
public enum MailUpdateSource
{
    /// <summary>
    /// Update originated from client-side UI changes (ApplyUIChanges).
    /// </summary>
    ClientUpdated,

    /// <summary>
    /// Update originated from client-side UI revert (RevertUIChanges).
    /// </summary>
    ClientReverted,

    /// <summary>
    /// Update originated from server synchronization or database operations.
    /// </summary>
    Server
}
