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
}
