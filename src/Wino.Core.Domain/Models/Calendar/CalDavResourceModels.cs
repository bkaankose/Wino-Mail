namespace Wino.Core.Domain.Models.Calendar;

public sealed class CalDavResourceSnapshot
{
    public string ExactHref { get; init; } = string.Empty;
    public string ETag { get; init; } = string.Empty;
    public string IcsContent { get; init; } = string.Empty;
}

public sealed class CalDavWriteRequest
{
    public string RemoteEventId { get; init; } = string.Empty;
    public string ExactHref { get; init; } = string.Empty;
    public string ETag { get; init; } = string.Empty;
    public string IcsContent { get; init; } = string.Empty;
    public bool CreateOnly { get; init; }
}

public sealed class CalDavWriteResult
{
    public string ExactHref { get; init; } = string.Empty;
    public string ETag { get; init; } = string.Empty;
    public bool RequiresRefetch { get; init; }
}
