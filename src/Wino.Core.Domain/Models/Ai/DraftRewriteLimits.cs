namespace Wino.Core.Domain.Models.Ai;

public static class DraftRewriteLimits
{
    /// <summary>
    /// The API's default <c>AiEmail:RewriteMaxInputChars</c>. A draft is checked against it before
    /// sending, so an oversized draft gets a clear error without spending a request.
    /// </summary>
    public const int MaximumHtmlLength = 120_000;
}
