using System;

namespace Wino.Core.Authentication;

/// <summary>
/// Parses the OAuth redirect response (the <c>code</c>/<c>state</c>/<c>error</c> query values) from a
/// redirect URL or raw query string. Shared by every capture mechanism — loopback listener and the
/// embedded WebView2 navigation interceptor — so they classify the redirect identically.
/// </summary>
public static class OAuthRedirectParser
{
    /// <summary>Parses a full redirect URL (e.g. <c>https://host/path?code=...&amp;state=...</c>).</summary>
    public static RedirectResult ParseUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return default;

        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
            return default;

        var query = url[(queryStart + 1)..];

        var fragment = query.IndexOf('#');
        if (fragment >= 0)
            query = query[..fragment];

        return ParseQuery(query);
    }

    /// <summary>Parses a raw query string (without the leading <c>?</c>).</summary>
    public static RedirectResult ParseQuery(string query)
        => new(GetValue(query, "code"), GetValue(query, "state"), GetValue(query, "error"));

    private static string GetValue(string query, string key)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals < 0)
                continue;

            if (pair[..equals] == key)
                return Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return null;
    }
}
