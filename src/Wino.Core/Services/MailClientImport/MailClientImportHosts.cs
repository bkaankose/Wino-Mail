#nullable enable
using System;
using System.Linq;

namespace Wino.Core.Services.MailClientImport;

/// <summary>The hosts and domains that make an imported account a Gmail or an Outlook account.</summary>
internal static class MailClientImportHosts
{
    private static readonly string[] MicrosoftCloudHosts =
    [
        "outlook.office365.com",
        "outlook.office.com",
        ".office365.com",
        ".outlook.com",
        "hotmail.com"
    ];

    private static readonly string[] GoogleHosts = ["imap.gmail.com", "pop.gmail.com", "imap.googlemail.com", "pop.googlemail.com"];
    private static readonly string[] GoogleDomains = ["gmail.com", "googlemail.com"];
    private static readonly string[] MicrosoftConsumerDomainPrefixes = ["outlook.", "hotmail.", "live.", "msn."];

    /// <summary>Whether a host, URL or registry text names a Microsoft 365 or Outlook.com server.</summary>
    public static bool IsMicrosoftCloud(string? text)
        => !string.IsNullOrWhiteSpace(text)
           && MicrosoftCloudHosts.Any(host => text.Contains(host, StringComparison.OrdinalIgnoreCase));

    public static bool IsMicrosoftConsumerDomain(string domain)
        => MicrosoftConsumerDomainPrefixes.Any(prefix => domain.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static bool IsGoogleHost(string? host)
        => !string.IsNullOrWhiteSpace(host) && GoogleHosts.Contains(host.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsGoogleDomain(string domain)
        => GoogleDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
}
