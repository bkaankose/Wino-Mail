#nullable enable
using System;

namespace Wino.Authentication;

/// <summary>
/// Redirect URI and callback parsing for Google OAuth clients that receive the redirect through a custom URL
/// scheme (iOS and Android clients). Google's scheme for such a client is the reversed client ID:
/// <c>123-abc.apps.googleusercontent.com</c> becomes <c>com.googleusercontent.apps.123-abc</c>, and the redirect is
/// <c>&lt;scheme&gt;:/oauthredirect</c>.
/// </summary>
public static class GoogleCustomSchemeRedirect
{
    private const string ClientIdSuffix = ".apps.googleusercontent.com";
    private const string SchemePrefix = "com.googleusercontent.apps.";
    private const string RedirectPath = "/oauthredirect";

    /// <summary>The reversed client ID scheme, lower case (URL schemes are case-insensitive).</summary>
    /// <exception cref="ArgumentException">The value is not a Google OAuth client ID.</exception>
    public static string GetScheme(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        var trimmed = clientId.Trim();
        if (!trimmed.EndsWith(ClientIdSuffix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The value is not a Google OAuth client ID.", nameof(clientId));

        var identifier = trimmed[..^ClientIdSuffix.Length];
        if (identifier.Length == 0 || !IsSchemeText(identifier))
            throw new ArgumentException("The value is not a Google OAuth client ID.", nameof(clientId));

        return (SchemePrefix + identifier).ToLowerInvariant();
    }

    /// <summary>The redirect URI to send in the authorization and token requests: <c>&lt;scheme&gt;:/oauthredirect</c>.</summary>
    public static Uri GetRedirectUri(string clientId) => new($"{GetScheme(clientId)}:{RedirectPath}");

    /// <summary>
    /// Reads the callback URL that the authentication session returned. Succeeds only when the scheme and path
    /// match <paramref name="redirectUri"/>, the state matches and a code is present.
    /// </summary>
    public static GoogleCustomSchemeCallback ParseCallback(Uri callbackUri, Uri redirectUri, string expectedState)
    {
        ArgumentNullException.ThrowIfNull(callbackUri);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentNullException.ThrowIfNull(expectedState);

        if (!callbackUri.IsAbsoluteUri
            || !string.Equals(callbackUri.Scheme, redirectUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(callbackUri.AbsolutePath.TrimEnd('/'), redirectUri.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal))
            return GoogleCustomSchemeCallback.Failed("invalid_redirect");

        string? state = null, code = null, error = null;
        var query = callbackUri.Query;
        if (query.StartsWith('?')) query = query[1..];
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString((separator < 0 ? pair : pair[..separator]).Replace('+', ' '));
            var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
            switch (name)
            {
                case "state": state ??= value; break;
                case "code": code ??= value; break;
                case "error": error ??= value; break;
            }
        }

        if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            return GoogleCustomSchemeCallback.Failed("invalid_state");
        if (!string.IsNullOrWhiteSpace(error))
            return GoogleCustomSchemeCallback.Failed(error);
        if (string.IsNullOrWhiteSpace(code))
            return GoogleCustomSchemeCallback.Failed("invalid_code");
        return new GoogleCustomSchemeCallback(code, null);
    }

    // RFC 3986 scheme characters after the first letter: ALPHA / DIGIT / "+" / "-" / ".".
    private static bool IsSchemeText(string value)
    {
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.'))
                return false;
        }
        return true;
    }
}

/// <summary>The outcome of <see cref="GoogleCustomSchemeRedirect.ParseCallback"/>: a code, or an OAuth-style error value.</summary>
public sealed record GoogleCustomSchemeCallback(string? Code, string? Error)
{
    public bool IsSuccess => Code is not null && Error is null;

    internal static GoogleCustomSchemeCallback Failed(string error) => new(null, error);
}
