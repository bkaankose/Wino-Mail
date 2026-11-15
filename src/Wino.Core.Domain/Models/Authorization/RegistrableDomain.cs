#nullable enable
using System;
using System.Collections.Generic;
using System.Net;

namespace Wino.Core.Domain.Models.Authorization;

/// <summary>
/// An approximation of the registrable domain (eTLD+1) used to decide whether a discovered issuer belongs to the mail
/// host's organization. It is not the Public Suffix List: it knows two-label country suffixes such as co.uk, com.au and
/// com.tr, and a short list of shared hosting and dynamic DNS suffixes where unrelated people own sibling names.
/// A suffix it does not know makes two tenants of that suffix look like one organization, so an unknown shared
/// suffix skips the issuer confirmation it should have asked for.
/// </summary>
public static class RegistrableDomain
{
    private static readonly HashSet<string> CountrySecondLevelLabels = new(StringComparer.Ordinal)
    {
        "ac", "co", "com", "edu", "gob", "go", "gov", "k12", "ltd", "mil", "ne", "net", "nom", "or", "org", "plc", "sch"
    };

    private static readonly HashSet<string> SharedSuffixes = new(StringComparer.Ordinal)
    {
        "github.io", "gitlab.io", "pages.dev", "workers.dev", "netlify.app", "vercel.app", "web.app", "firebaseapp.com",
        "appspot.com", "herokuapp.com", "azurewebsites.net", "cloudapp.net", "cloudapp.azure.com", "amazonaws.com",
        "cloudfront.net", "onrender.com", "fly.dev", "duckdns.org", "no-ip.org", "no-ip.biz", "ddns.net", "hopto.org",
        "zapto.org", "dyndns.org", "dynu.net", "freeddns.org", "synology.me", "myqnapcloud.com", "tplinkdns.com",
        "ts.net", "ngrok.io", "ngrok-free.app", "trycloudflare.com"
    };

    public static string Get(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0 || IPAddress.TryParse(normalized.Trim('[', ']'), out _))
            return normalized;

        var labels = normalized.Split('.');
        if (labels.Length <= 2)
            return normalized;

        var suffixLabels = 1;
        foreach (var shared in SharedSuffixes)
        {
            if (normalized == shared || normalized.EndsWith("." + shared, StringComparison.Ordinal))
            {
                suffixLabels = Math.Max(suffixLabels, shared.Split('.').Length);
            }
        }

        if (suffixLabels == 1 && labels[^1].Length == 2 && CountrySecondLevelLabels.Contains(labels[^2]))
            suffixLabels = 2;

        var keep = Math.Min(labels.Length, suffixLabels + 1);
        return string.Join('.', labels[^keep..]);
    }

    public static bool AreSame(string leftHost, string rightHost)
        => string.Equals(Get(leftHost), Get(rightHost), StringComparison.Ordinal);
}
