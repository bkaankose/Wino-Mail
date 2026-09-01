using System;

namespace Wino.Core.Authentication;

/// <summary>Parses OAuth redirect query values.</summary>
public static class OAuthRedirectParser
{
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
