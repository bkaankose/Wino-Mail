using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.CardDav;

public sealed class DavRequestException : Exception
{
    public int StatusCode { get; }
    public IReadOnlyList<string> ErrorNames { get; }
    public TimeSpan? RetryAfter { get; }

    public DavRequestException(
        int statusCode,
        string message,
        IReadOnlyList<string> errorNames = null,
        TimeSpan? retryAfter = null,
        Exception innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorNames = errorNames ?? [];
        RetryAfter = retryAfter;
    }

    public bool HasError(string localName)
        => ErrorNames is not null && System.Linq.Enumerable.Any(ErrorNames,
            value => value.EndsWith($"}}{localName}", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the server refused the form of the request rather than the account or the
    /// resource: a REPORT it does not implement (RFC 3253 DAV:supported-report), a body element
    /// it does not parse, or a method it does not allow. The caller can fall back to a plainer
    /// request; retrying the same one cannot succeed.
    /// </summary>
    public bool IsUnsupportedRequest
        => StatusCode is 400 or 405 or 415 or 422 or 501 ||
           (StatusCode == 403 && HasError("supported-report"));
}
