#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Services.MailClientImport;

/// <summary>
/// Reads the accounts of every Thunderbird profile from its prefs.js. Passwords live in logins.json, encrypted with the
/// profile's key; that file is never opened.
/// </summary>
internal static class ThunderbirdAccountReader
{
    public static List<MailClientAccountFinding> Read(IEnumerable<string> roots)
    {
        var findings = new List<MailClientAccountFinding>();

        foreach (var profileFolder in roots.SelectMany(FindProfileFolders).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var preferences = ReadPreferences(Path.Combine(profileFolder, "prefs.js"));
            if (preferences.Count > 0)
                findings.AddRange(ReadAccounts(preferences));
        }

        return findings;
    }

    private static IEnumerable<string> FindProfileFolders(string root)
    {
        var folders = new List<string>();

        if (KeyFileDocument.TryLoad(Path.Combine(root, "profiles.ini"), glibEscapes: false) is { } profiles)
        {
            foreach (var section in profiles.SectionNames.Where(s => s.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)))
            {
                var path = profiles.Get(section, "Path");
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var isRelative = profiles.Get(section, "IsRelative") != "0";
                folders.Add(Path.GetFullPath(isRelative
                    ? Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))
                    : path));
            }
        }

        // A profile folder that profiles.ini no longer lists still holds accounts the user may want.
        var profilesFolder = Path.Combine(root, "Profiles");
        try
        {
            if (Directory.Exists(profilesFolder))
                folders.AddRange(Directory.EnumerateDirectories(profilesFolder).Select(Path.GetFullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return folders;
    }

    internal static Dictionary<string, string> ReadPreferences(string path)
    {
        var preferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string content;
        try
        {
            if (!File.Exists(path))
                return preferences;

            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return preferences;
        }

        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (TryParsePreference(line, out var key, out var value))
                preferences[key] = value;
        }

        return preferences;
    }

    private static List<MailClientAccountFinding> ReadAccounts(Dictionary<string, string> preferences)
    {
        var findings = new List<MailClientAccountFinding>();

        var accountIds = Split(Get(preferences, "mail.accountmanager.accounts"));
        if (accountIds.Count == 0)
        {
            accountIds = preferences.Keys
                .Where(k => k.StartsWith("mail.account.", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".server", StringComparison.OrdinalIgnoreCase))
                .Select(k => k["mail.account.".Length..^".server".Length])
                .ToList();
        }

        foreach (var accountId in accountIds)
        {
            var serverId = Get(preferences, $"mail.account.{accountId}.server");
            if (string.IsNullOrEmpty(serverId))
                continue;

            var server = $"mail.server.{serverId}.";
            var type = Get(preferences, server + "type").ToLowerInvariant();

            var kind = type switch
            {
                "imap" => MailClientAccountKind.Imap,
                "pop3" => MailClientAccountKind.Pop3,
                "ews" => MailClientAccountKind.Exchange,
                _ when type.StartsWith("owl", StringComparison.Ordinal) => MailClientAccountKind.Exchange,
                _ => (MailClientAccountKind?)null
            };

            // Local Folders ("none"), news, feeds and movemail are not mail accounts Wino can add.
            if (kind is not { } accountKind)
                continue;

            var identity = Split(Get(preferences, $"mail.account.{accountId}.identities")).FirstOrDefault() ?? string.Empty;
            var identityPrefix = $"mail.identity.{identity}.";

            var incoming = new MailClientServerEndpoint
            {
                Host = FirstNonEmpty(Get(preferences, server + "realhostname"), Get(preferences, server + "hostname")),
                Port = GetInt(preferences, server + "port"),
                Security = ToSecurity(GetInt(preferences, server + "socketType")),
                Authentication = ToAuthentication(GetInt(preferences, server + "authMethod")),
                Username = FirstNonEmpty(Get(preferences, server + "realuserName"), Get(preferences, server + "userName"))
            };

            var address = FirstNonEmpty(Get(preferences, identityPrefix + "useremail"), incoming.Username.Contains('@') ? incoming.Username : string.Empty);

            var finding = new MailClientAccountFinding
            {
                Source = MailClientImportSource.Thunderbird,
                Kind = accountKind,
                Address = address,
                AccountName = Get(preferences, server + "name"),
                SenderName = Get(preferences, identityPrefix + "fullName"),
                Incoming = incoming,
                Outgoing = ReadOutgoing(preferences, identityPrefix)
            };

            if (accountKind == MailClientAccountKind.Exchange)
            {
                finding.ExchangeServer = FirstNonEmpty(Get(preferences, server + "ews_url"), Get(preferences, server + "ewsURL"), incoming.Host);
                finding.ExchangeTransport = type.Contains("eas", StringComparison.Ordinal) ? ExchangeTransport.ActiveSync : ExchangeTransport.Ews;
            }

            findings.Add(finding);
        }

        return findings;
    }

    // Older releases keep outgoing servers under mail.smtpserver; newer ones also use mail.outgoingserver.
    private static MailClientServerEndpoint? ReadOutgoing(Dictionary<string, string> preferences, string identityPrefix)
    {
        var serverId = FirstNonEmpty(
            Get(preferences, identityPrefix + "smtpServer"),
            Get(preferences, identityPrefix + "outgoingServer"),
            Get(preferences, "mail.smtp.defaultserver"),
            Get(preferences, "mail.outgoingserver.defaultserver"));

        if (string.IsNullOrEmpty(serverId))
            return null;

        foreach (var prefix in new[] { $"mail.smtpserver.{serverId}.", $"mail.outgoingserver.{serverId}." })
        {
            var host = Get(preferences, prefix + "hostname");
            if (string.IsNullOrEmpty(host))
                continue;

            return new MailClientServerEndpoint
            {
                Host = host,
                Port = GetInt(preferences, prefix + "port"),
                Security = ToSecurity(GetInt(preferences, prefix + "try_ssl")),
                Authentication = ToAuthentication(GetInt(preferences, prefix + "authMethod")),
                Username = Get(preferences, prefix + "username")
            };
        }

        return null;
    }

    // nsMsgSocketType: 0 plain, 1 STARTTLS when available, 2 always STARTTLS, 3 SSL/TLS.
    private static ImapConnectionSecurity ToSecurity(int? socketType) => socketType switch
    {
        0 => ImapConnectionSecurity.None,
        1 or 2 => ImapConnectionSecurity.StartTls,
        3 => ImapConnectionSecurity.SslTls,
        _ => ImapConnectionSecurity.Auto
    };

    // nsMsgAuthMethod: 1 none, 3 cleartext password, 4 encrypted password, 6 NTLM, 10 OAuth2.
    private static ImapAuthenticationMethod ToAuthentication(int? authMethod) => authMethod switch
    {
        1 => ImapAuthenticationMethod.None,
        3 => ImapAuthenticationMethod.NormalPassword,
        4 => ImapAuthenticationMethod.EncryptedPassword,
        6 => ImapAuthenticationMethod.Ntlm,
        10 => ImapAuthenticationMethod.OAuth2,
        _ => ImapAuthenticationMethod.Auto
    };

    internal static bool TryParsePreference(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var trimmed = line.Trim();
        const string Prefix = "user_pref(";
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal) || !trimmed.EndsWith(");", StringComparison.Ordinal))
            return false;

        var body = trimmed[Prefix.Length..^2];
        var position = 0;
        if (!TryReadString(body, ref position, out key))
            return false;

        while (position < body.Length && (body[position] == ',' || char.IsWhiteSpace(body[position])))
            position++;

        if (position >= body.Length)
            return false;

        if (body[position] == '"')
            return TryReadString(body, ref position, out value);

        value = body[position..].Trim();
        return value.Length > 0;
    }

    private static bool TryReadString(string text, ref int position, out string value)
    {
        value = string.Empty;
        if (position >= text.Length || text[position] != '"')
            return false;

        var builder = new StringBuilder();
        position++;

        while (position < text.Length)
        {
            var character = text[position++];
            if (character == '"')
            {
                value = builder.ToString();
                return true;
            }

            if (character != '\\' || position >= text.Length)
            {
                builder.Append(character);
                continue;
            }

            var escaped = text[position++];
            switch (escaped)
            {
                case 'n': builder.Append('\n'); break;
                case 't': builder.Append('\t'); break;
                case 'r': builder.Append('\r'); break;
                case 'u' when position + 4 <= text.Length
                              && int.TryParse(text.AsSpan(position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    builder.Append((char)code);
                    position += 4;
                    break;
                default: builder.Append(escaped); break;
            }
        }

        return false;
    }

    private static string Get(Dictionary<string, string> preferences, string key)
        => preferences.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private static int? GetInt(Dictionary<string, string> preferences, string key)
        => int.TryParse(Get(preferences, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static List<string> Split(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
}
