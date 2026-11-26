#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Services.MailClientImport;

/// <summary>
/// Reads the accounts of every classic Outlook (Win32) mail profile from the registry. Each account is a subkey of the
/// profile's account manager section; text values are stored as UTF-16 binaries. Saved passwords are DPAPI blobs in
/// the same keys and are never read.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class OutlookClassicAccountReader
{
    private const string AccountManagerSection = "9375CFF0413111d3B88A00104B2A6676";

    private static readonly string[] ProfileRoots =
    [
        @"Software\Microsoft\Office\16.0\Outlook\Profiles",
        @"Software\Microsoft\Office\15.0\Outlook\Profiles",
        @"Software\Microsoft\Windows NT\CurrentVersion\Windows Messaging Subsystem\Profiles"
    ];

    public static List<MailClientAccountFinding> Read()
    {
        var findings = new List<MailClientAccountFinding>();

        foreach (var root in ProfileRoots)
        {
            try
            {
                using var profiles = Registry.CurrentUser.OpenSubKey(root);
                if (profiles == null)
                    continue;

                foreach (var profileName in profiles.GetSubKeyNames())
                {
                    using var profile = profiles.OpenSubKey(profileName);
                    using var accounts = profile?.OpenSubKey(AccountManagerSection);
                    if (profile == null || accounts == null)
                        continue;

                    foreach (var accountKeyName in accounts.GetSubKeyNames())
                    {
                        using var account = accounts.OpenSubKey(accountKeyName);
                        if (account != null && ReadAccount(profile, account) is { } finding)
                            findings.Add(finding);
                    }
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        return findings;
    }

    private static MailClientAccountFinding? ReadAccount(RegistryKey profile, RegistryKey account)
    {
        var accountName = ReadText(account, "Account Name");
        var email = ReadText(account, "Email");
        var displayName = ReadText(account, "Display Name");

        var finding = new MailClientAccountFinding
        {
            Source = MailClientImportSource.OutlookClassic,
            AccountName = accountName,
            SenderName = displayName,
            Address = !string.IsNullOrWhiteSpace(email) ? email : (accountName.Contains('@') ? accountName : string.Empty)
        };

        var imapServer = ReadText(account, "IMAP Server");
        var popServer = ReadText(account, "POP3 Server");
        var easServer = ReadText(account, "EAS Server URL");

        if (!string.IsNullOrWhiteSpace(imapServer))
        {
            finding.Kind = MailClientAccountKind.Imap;
            finding.Incoming = ReadIncoming(account, "IMAP", imapServer);
            finding.Outgoing = ReadOutgoing(account);
        }
        else if (!string.IsNullOrWhiteSpace(popServer))
        {
            finding.Kind = MailClientAccountKind.Pop3;
            finding.Incoming = ReadIncoming(account, "POP3", popServer);
            finding.Outgoing = ReadOutgoing(account);
        }
        else if (!string.IsNullOrWhiteSpace(easServer))
        {
            finding.Kind = MailClientImportHosts.IsMicrosoftCloud(easServer) ? MailClientAccountKind.Outlook : MailClientAccountKind.Exchange;
            finding.ExchangeServer = easServer;
            finding.ExchangeTransport = ExchangeTransport.ActiveSync;
        }
        else if (account.GetValue("Service UID") is byte[] serviceUid && serviceUid.Length == 16)
        {
            // An Exchange (MAPI) account. Microsoft 365 and on-premises servers look the same here, so the service's
            // own profile section decides: it names the Microsoft 365 host when the mailbox is in the cloud.
            finding.Kind = ServiceMentionsMicrosoftCloud(profile, serviceUid)
                ? MailClientAccountKind.Outlook
                : MailClientAccountKind.Exchange;
            finding.ExchangeTransport = ExchangeTransport.MapiHttp;
        }
        else
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(finding.Address) ? null : finding;
    }

    private static MailClientServerEndpoint ReadIncoming(RegistryKey account, string protocol, string host)
    {
        var port = ReadNumber(account, $"{protocol} Port");
        var security = ReadNumber(account, $"{protocol} Use SSL");

        return new MailClientServerEndpoint
        {
            Host = host,
            Port = port,
            Security = security switch
            {
                0 => ImapConnectionSecurity.None,
                1 => ImapConnectionSecurity.SslTls,
                2 => ImapConnectionSecurity.StartTls,
                _ => ImapConnectionSecurity.Auto
            },
            Authentication = ReadNumber(account, $"{protocol} Use SPA") == 1 ? ImapAuthenticationMethod.Ntlm : ImapAuthenticationMethod.Auto,
            Username = ReadText(account, $"{protocol} User")
        };
    }

    // SMTP Secure Connection: 0 none, 1 SSL/TLS, 2 STARTTLS, 3 automatic.
    private static MailClientServerEndpoint? ReadOutgoing(RegistryKey account)
    {
        var host = ReadText(account, "SMTP Server");
        if (string.IsNullOrWhiteSpace(host))
            return null;

        var security = ReadNumber(account, "SMTP Secure Connection") ?? (ReadNumber(account, "SMTP Use SSL") == 1 ? 1 : null);

        return new MailClientServerEndpoint
        {
            Host = host,
            Port = ReadNumber(account, "SMTP Port"),
            Security = security switch
            {
                0 => ImapConnectionSecurity.None,
                1 => ImapConnectionSecurity.SslTls,
                2 => ImapConnectionSecurity.StartTls,
                _ => ImapConnectionSecurity.Auto
            },
            Authentication = ReadNumber(account, "SMTP Use SPA") == 1 ? ImapAuthenticationMethod.Ntlm : ImapAuthenticationMethod.Auto,
            Username = ReadText(account, "SMTP User")
        };
    }

    private static bool ServiceMentionsMicrosoftCloud(RegistryKey profile, byte[] serviceUid)
    {
        using var service = profile.OpenSubKey(Convert.ToHexString(serviceUid).ToLowerInvariant());
        if (service == null)
            return false;

        foreach (var valueName in service.GetValueNames())
        {
            if (MailClientImportHosts.IsMicrosoftCloud(DecodeText(service.GetValue(valueName))))
                return true;
        }

        return false;
    }

    private static string ReadText(RegistryKey key, string name) => DecodeText(key.GetValue(name)).Trim();

    private static int? ReadNumber(RegistryKey key, string name) => key.GetValue(name) switch
    {
        int number => number,
        byte[] { Length: 4 } bytes => BitConverter.ToInt32(bytes, 0),
        _ => null
    };

    private static string DecodeText(object? value) => value switch
    {
        string text => text,
        byte[] bytes when bytes.Length >= 2 => Encoding.Unicode.GetString(bytes).TrimEnd('\0'),
        _ => string.Empty
    };
}
