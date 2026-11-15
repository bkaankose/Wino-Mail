#nullable enable
using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// The address domains and mail hosts that decide an account route without asking the user: Google and Microsoft
/// consumer domains, their hosted-mail MX hosts and IMAP hosts, and the mail filters that hide a Microsoft 365 tenant.
/// </summary>
public static class KnownMailHosts
{
    private static readonly string[] GoogleConsumerDomains = ["gmail.com", "googlemail.com"];

    /// <summary>Microsoft consumer brands, matched as <c>brand.tld</c> or <c>brand.cc-sld.tld</c> (outlook.com.tr, live.co.uk).</summary>
    private static readonly string[] MicrosoftConsumerBrands = ["outlook", "hotmail", "live"];

    private static readonly string[] MicrosoftConsumerDomains = ["msn.com"];

    private static readonly string[] GoogleMxSuffixes = ["google.com", "googlemail.com"];

    /// <summary><c>mx.microsoft</c> is the DNSSEC-signed MX namespace Microsoft 365 moved new tenants to in 2024.</summary>
    private static readonly string[] MicrosoftMxSuffixes = ["mail.protection.outlook.com", "mx.microsoft"];

    /// <summary>
    /// Mail filters that sit in front of hosted mailboxes: Proofpoint (and Proofpoint Essentials), Mimecast,
    /// Barracuda, Cisco Secure Email and Broadcom (Symantec) MessageLabs. Only these send the address to Microsoft's
    /// user-realm endpoint.
    /// </summary>
    private static readonly string[] MailFilterMxSuffixes =
        ["pphosted.com", "ppe-hosted.com", "mimecast.com", "barracudanetworks.com", "iphmx.com", "messagelabs.com"];

    private static readonly string[] GoogleImapHosts = ["imap.gmail.com", "imap.googlemail.com"];

    private static readonly string[] MicrosoftImapHosts = ["outlook.office365.com", "imap-mail.outlook.com", "outlook.office.com"];

    /// <summary>Exchange Online's EWS hosts, worldwide and in the national clouds.</summary>
    private static readonly string[] MicrosoftExchangeOnlineSuffixes = ["outlook.office365.com", "outlook.office.com", "office365.us", "outlook.cn", "outlook.com"];

    /// <summary>Gmail or Outlook when the address domain belongs to their consumer service, otherwise null.</summary>
    public static MailProviderType? GetConsumerProvider(string domain)
    {
        var normalized = Normalize(domain);
        if (normalized.Length == 0)
            return null;

        if (Array.Exists(GoogleConsumerDomains, candidate => candidate == normalized))
            return MailProviderType.Gmail;

        if (Array.Exists(MicrosoftConsumerDomains, candidate => candidate == normalized))
            return MailProviderType.Outlook;

        var labels = normalized.Split('.');
        if (labels.Length is < 2 or > 3 || !Array.Exists(MicrosoftConsumerBrands, brand => brand == labels[0]))
            return null;

        // outlook.example.org is somebody else's domain; outlook.com.tr and live.co.uk are Microsoft's.
        var isCountrySuffix = labels.Length == 2 || (labels[2].Length == 2 && labels[1].Length <= 3);
        return isCountrySuffix ? MailProviderType.Outlook : null;
    }

    /// <summary>Gmail for Google Workspace MX hosts, Outlook for Microsoft 365 MX hosts, otherwise null.</summary>
    public static MailProviderType? GetHostedMailProviderByMx(string mxHost)
    {
        var normalized = Normalize(mxHost);

        if (HasAnySuffix(normalized, GoogleMxSuffixes))
            return MailProviderType.Gmail;

        if (HasAnySuffix(normalized, MicrosoftMxSuffixes))
            return MailProviderType.Outlook;

        return null;
    }

    public static bool IsMailFilterHost(string mxHost)
        => HasAnySuffix(Normalize(mxHost), MailFilterMxSuffixes);

    /// <summary>Gmail or Outlook when a discovered IMAP host is theirs, otherwise null.</summary>
    public static MailProviderType? GetProviderByImapHost(string imapHost)
    {
        var normalized = Normalize(imapHost);
        if (normalized.Length == 0)
            return null;

        if (Array.Exists(GoogleImapHosts, candidate => candidate == normalized))
            return MailProviderType.Gmail;

        if (Array.Exists(MicrosoftImapHosts, candidate => candidate == normalized))
            return MailProviderType.Outlook;

        return null;
    }

    /// <summary>An EWS endpoint that Exchange Autodiscover returned belongs to Microsoft 365, not to an on-premises server.</summary>
    public static bool IsExchangeOnlineHost(string host)
        => HasAnySuffix(Normalize(host), MicrosoftExchangeOnlineSuffixes);

    private static bool HasAnySuffix(string host, string[] suffixes)
        => host.Length > 0 && Array.Exists(suffixes, suffix => host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal));

    private static string Normalize(string? value)
        => value?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
}
