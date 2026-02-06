namespace Wino.Core.Domain.Enums;

/// <summary>
/// Classifies the severity of synchronization errors to determine retry behavior.
/// </summary>
public enum SynchronizerErrorSeverity
{
    /// <summary>
    /// Transient error that should be retried with exponential backoff.
    /// Examples: network timeout, temporary server unavailability, rate limiting.
    /// </summary>
    Transient,

    /// <summary>
    /// Error that can be recovered from by skipping the affected item/folder and continuing sync.
    /// Examples: folder deleted externally, message not found, permission denied on single item.
    /// </summary>
    Recoverable,

    /// <summary>
    /// Fatal error that requires stopping synchronization and user intervention.
    /// Examples: account disabled, server permanently unavailable, critical configuration error.
    /// </summary>
    Fatal,

    /// <summary>
    /// Authentication error that requires the user to re-authenticate.
    /// Examples: token expired, password changed, OAuth refresh failed.
    /// </summary>
    AuthRequired
}
