namespace Wino.Core.Domain.Enums;

/// <summary>
/// Indicates the source of a mail update.
/// </summary>
public enum MailUpdateSource
{
    /// <summary>
    /// Update originated from client-side UI changes (ApplyUIChanges/RevertUIChanges).
    /// </summary>
    Client,

    /// <summary>
    /// Update originated from server synchronization or database operations.
    /// </summary>
    Server
}
