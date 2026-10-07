using System;

namespace Wino.Services.Dav;

/// <summary>
/// Resolves DAV hrefs and redirect locations against the request URI. On macOS and Linux .NET parses
/// a rooted path such as "/123/principal/" as an absolute file:// URI, so "absolute" alone does not
/// mean the value names a server; only http and https URIs are used as they are.
/// </summary>
public static class DavUri
{
    public static Uri Resolve(Uri baseUri, string href)
        => Uri.TryCreate(href, UriKind.Absolute, out var absolute) && IsHttp(absolute)
            ? absolute
            : new Uri(baseUri, href);

    public static Uri Resolve(Uri baseUri, Uri location)
        => location.IsAbsoluteUri && IsHttp(location)
            ? location
            : new Uri(baseUri, location.OriginalString);

    private static bool IsHttp(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
