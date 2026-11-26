#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Services.MailClientImport;

/// <summary>
/// Reads GNOME Online Accounts' accounts.conf, which Evolution, Geary and GNOME's own apps share. Accounts with mail
/// turned off are skipped. Secrets live in the Secret Service and are never read.
/// </summary>
internal static class GnomeOnlineAccountsReader
{
    public static List<MailClientAccountFinding> Read(string path)
    {
        var findings = new List<MailClientAccountFinding>();
        if (KeyFileDocument.TryLoad(path, glibEscapes: true) is not { } document)
            return findings;

        foreach (var section in document.SectionNames.Where(s => s.StartsWith("Account ", StringComparison.Ordinal)))
        {
            if (document.GetBool(section, "MailEnabled") == false)
                continue;

            var provider = document.Get(section, "Provider").ToLowerInvariant();
            var identity = FirstAddress(
                document.Get(section, "EmailAddress"),
                document.Get(section, "PresentationIdentity"),
                document.Get(section, "Identity"));

            if (string.IsNullOrWhiteSpace(identity))
                continue;

            var finding = new MailClientAccountFinding
            {
                Source = MailClientImportSource.GnomeOnlineAccounts,
                Address = identity,
                SenderName = document.Get(section, "Name")
            };

            switch (provider)
            {
                case "google":
                    finding.Kind = MailClientAccountKind.Gmail;
                    break;
                case "ms_graph" or "windows_live" or "microsoft365":
                    finding.Kind = MailClientAccountKind.Outlook;
                    break;
                case "exchange":
                    finding.Kind = MailClientAccountKind.Exchange;
                    finding.ExchangeServer = document.Get(section, "Host");
                    finding.ExchangeTransport = ExchangeTransport.Ews;
                    break;
                case "imap_smtp":
                    finding.Kind = MailClientAccountKind.Imap;
                    finding.Incoming = ReadEndpoint(document, section, "Imap");
                    finding.Outgoing = ReadEndpoint(document, section, "Smtp");
                    break;
                default:
                    continue;
            }

            findings.Add(finding);
        }

        return findings;
    }

    private static MailClientServerEndpoint ReadEndpoint(KeyFileDocument document, string section, string prefix)
    {
        var (host, port) = SplitHostAndPort(document.Get(section, prefix + "Host"));

        return new MailClientServerEndpoint
        {
            Host = host,
            Port = port,
            Username = document.Get(section, prefix + "UserName"),
            Security = document.GetBool(section, prefix + "UseSsl") == true
                ? ImapConnectionSecurity.SslTls
                : document.GetBool(section, prefix + "UseTls") == true
                    ? ImapConnectionSecurity.StartTls
                    : ImapConnectionSecurity.Auto,
            Authentication = prefix == "Smtp" && document.GetBool(section, "SmtpUseAuth") == false
                ? ImapAuthenticationMethod.None
                : ImapAuthenticationMethod.Auto
        };
    }

    private static (string Host, int? Port) SplitHostAndPort(string value)
    {
        var separator = value.LastIndexOf(':');
        if (separator > 0 && !value.Contains(']', StringComparison.Ordinal)
            && int.TryParse(value[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            return (value[..separator], port);

        return (value, null);
    }

    private static string FirstAddress(params string[] values)
        => values.FirstOrDefault(v => v.Contains('@')) ?? string.Empty;
}
