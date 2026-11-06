#nullable enable
using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Authentication;

/// <summary>The outcome of an embedded browser sign-in. <see cref="RedirectUri"/> is set only on success.</summary>
public sealed record EmbeddedBrowserAuthResult(EmbeddedBrowserAuthStatus Status, Uri? RedirectUri)
{
    public static EmbeddedBrowserAuthResult Canceled { get; } = new(EmbeddedBrowserAuthStatus.Canceled, null);

    public static EmbeddedBrowserAuthResult Failed { get; } = new(EmbeddedBrowserAuthStatus.Failed, null);

    public static EmbeddedBrowserAuthResult Success(Uri redirectUri) => new(EmbeddedBrowserAuthStatus.Success, redirectUri);

    /// <summary>
    /// Whether <paramref name="candidate"/> is the redirect address: scheme, host, port and path must match, while the
    /// query and fragment carry the response. A prefix match would let https://host/callbackEvil pass for
    /// https://host/callback.
    /// </summary>
    public static bool IsRedirect(string? candidate, Uri redirectUri)
    {
        ArgumentNullException.ThrowIfNull(redirectUri);

        return !string.IsNullOrEmpty(candidate)
               && Uri.TryCreate(candidate, UriKind.Absolute, out var actual)
               && Uri.Compare(actual, redirectUri,
                   UriComponents.SchemeAndServer | UriComponents.Path,
                   UriFormat.Unescaped,
                   StringComparison.OrdinalIgnoreCase) == 0;
    }
}
