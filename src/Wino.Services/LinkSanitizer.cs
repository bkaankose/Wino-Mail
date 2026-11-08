using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class LinkSanitizer : ILinkSanitizer
{
    private static readonly HashSet<string> TrackingParameters = new(Constants.LinkTrackingParameters, StringComparer.OrdinalIgnoreCase);

    public Uri Clean(Uri uri, out int removedCount)
    {
        ArgumentNullException.ThrowIfNull(uri);

        removedCount = 0;

        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Query)
            || uri.Query == "?")
        {
            return uri;
        }

        // Work on the raw query so the kept parameters stay byte for byte as the sender encoded them.
        var parts = uri.Query[1..].Split('&');
        var kept = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (IsTrackingParameter(part))
                removedCount++;
            else
                kept.Add(part);
        }

        if (removedCount == 0)
            return uri;

        var builder = new UriBuilder(uri)
        {
            Query = string.Join('&', kept.Where(a => a.Length > 0))
        };

        return builder.Uri;
    }

    private static bool IsTrackingParameter(string part)
    {
        if (part.Length == 0)
            return false;

        var separator = part.IndexOf('=');
        var encodedKey = separator >= 0 ? part[..separator] : part;

        string key;
        try
        {
            key = Uri.UnescapeDataString(encodedKey.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            key = encodedKey;
        }

        return key.StartsWith(Constants.LinkTrackingParameterPrefix, StringComparison.OrdinalIgnoreCase)
               || TrackingParameters.Contains(key);
    }
}
