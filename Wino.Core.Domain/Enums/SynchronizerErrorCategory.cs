namespace Wino.Core.Domain.Enums;

/// <summary>
/// Categorizes synchronization errors by their root cause for targeted handling.
/// </summary>
public enum SynchronizerErrorCategory
{
    /// <summary>
    /// Network-related issues: connection timeouts, DNS failures, socket errors.
    /// </summary>
    Network,

    /// <summary>
    /// Authentication failures: invalid credentials, expired tokens, revoked access.
    /// </summary>
    Authentication,

    /// <summary>
    /// Rate limiting: too many requests (HTTP 429), quota exceeded.
    /// </summary>
    RateLimit,

    /// <summary>
    /// Resource not found: folder or message deleted externally (HTTP 404).
    /// </summary>
    ResourceNotFound,

    /// <summary>
    /// Server errors: internal server errors (HTTP 5xx), service unavailable.
    /// </summary>
    ServerError,

    /// <summary>
    /// Protocol errors: IMAP/SMTP command failures, malformed responses.
    /// </summary>
    ProtocolError,

    /// <summary>
    /// Validation errors: invalid data, constraint violations.
    /// </summary>
    Validation,

    /// <summary>
    /// Unknown or unclassified error.
    /// </summary>
    Unknown
}
