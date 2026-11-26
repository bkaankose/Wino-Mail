#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Services.MailClientImport;

/// <summary>
/// Reads Evolution's mail accounts from its ESource key files: a mail account source names its identity source, and the
/// identity names its outgoing transport source. Passwords live in the Secret Service and are never read.
/// </summary>
internal static class EvolutionAccountReader
{
    public static List<MailClientAccountFinding> Read(IEnumerable<string> sourceFolders)
    {
        var findings = new List<MailClientAccountFinding>();

        foreach (var folder in sourceFolders)
        {
            var sources = LoadSources(folder);
            if (sources.Count == 0)
                continue;

            foreach (var (_, source) in sources)
            {
                if (ReadAccount(source, sources) is { } finding)
                    findings.Add(finding);
            }
        }

        return findings;
    }

    private static Dictionary<string, KeyFileDocument> LoadSources(string folder)
    {
        var sources = new Dictionary<string, KeyFileDocument>(StringComparer.Ordinal);

        try
        {
            if (!Directory.Exists(folder))
                return sources;

            foreach (var path in Directory.EnumerateFiles(folder, "*.source"))
            {
                if (KeyFileDocument.TryLoad(path, glibEscapes: true) is { } document)
                    sources[Path.GetFileNameWithoutExtension(path)] = document;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return sources;
    }

    private static MailClientAccountFinding? ReadAccount(KeyFileDocument source, Dictionary<string, KeyFileDocument> sources)
    {
        if (!source.HasSection("Mail Account") || source.GetBool("Data Source", "Enabled") == false)
            return null;

        var backend = source.Get("Mail Account", "BackendName").ToLowerInvariant();
        var kind = backend switch
        {
            "imapx" or "imap" => MailClientAccountKind.Imap,
            "pop" => MailClientAccountKind.Pop3,
            "ews" => MailClientAccountKind.Exchange,
            _ => (MailClientAccountKind?)null
        };

        if (kind is not { } accountKind)
            return null;

        var identity = sources.GetValueOrDefault(source.Get("Mail Account", "IdentityUid"));
        var transport = identity == null ? null : sources.GetValueOrDefault(identity.Get("Mail Submission", "TransportUid"));

        var address = identity?.Get("Mail Identity", "Address") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(address) && accountKind == MailClientAccountKind.Exchange)
            address = source.Get("Ews Backend", "Email");

        var finding = new MailClientAccountFinding
        {
            Source = MailClientImportSource.Evolution,
            Kind = accountKind,
            Address = address,
            AccountName = source.Get("Data Source", "DisplayName"),
            SenderName = identity?.Get("Mail Identity", "Name") ?? string.Empty,
            Incoming = ReadEndpoint(source),
            Outgoing = transport?.Get("Mail Transport", "BackendName").Equals("smtp", StringComparison.OrdinalIgnoreCase) == true
                ? ReadEndpoint(transport)
                : null
        };

        if (accountKind == MailClientAccountKind.Exchange)
        {
            finding.ExchangeServer = source.Get("Ews Backend", "HostUrl");
            finding.ExchangeTransport = ExchangeTransport.Ews;

            // Office 365 accounts in Evolution sign in with OAuth2 against Microsoft's EWS endpoint.
            if (MailClientImportHosts.IsMicrosoftCloud(finding.ExchangeServer))
                finding.Kind = MailClientAccountKind.Outlook;
        }

        return finding;
    }

    private static MailClientServerEndpoint ReadEndpoint(KeyFileDocument source) => new()
    {
        Host = source.Get("Authentication", "Host"),
        Port = source.GetInt("Authentication", "Port") is > 0 and var port ? port : null,
        Username = source.Get("Authentication", "User"),
        Authentication = ToAuthentication(source.Get("Authentication", "Method")),
        Security = source.Get("Security", "Method").ToLowerInvariant() switch
        {
            "none" => ImapConnectionSecurity.None,
            "ssl-on-alternate-port" => ImapConnectionSecurity.SslTls,
            "starttls-on-standard-port" => ImapConnectionSecurity.StartTls,
            _ => ImapConnectionSecurity.Auto
        }
    };

    internal static ImapAuthenticationMethod ToAuthentication(string method)
    {
        var normalized = method.Trim().ToUpperInvariant();
        return normalized switch
        {
            "PLAIN" or "LOGIN" or "PASSWORD" => ImapAuthenticationMethod.NormalPassword,
            "CRAM-MD5" => ImapAuthenticationMethod.CramMd5,
            "DIGEST-MD5" => ImapAuthenticationMethod.DigestMd5,
            "NTLM" => ImapAuthenticationMethod.Ntlm,
            _ when normalized.Contains("OAUTH", StringComparison.Ordinal) || normalized is "GOOGLE" or "OUTLOOK" => ImapAuthenticationMethod.OAuth2,
            _ => ImapAuthenticationMethod.Auto
        };
    }
}
