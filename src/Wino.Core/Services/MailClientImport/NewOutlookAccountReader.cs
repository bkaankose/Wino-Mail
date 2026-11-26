#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Services.MailClientImport;

/// <summary>
/// Reads the accounts of the new Outlook for Windows from its UserSettings.json. Microsoft keeps the servers of these
/// accounts in its cloud, so the file holds addresses and account types only; servers are discovered on import. The
/// format is undocumented and is read defensively.
/// </summary>
internal static class NewOutlookAccountReader
{
    public static List<MailClientAccountFinding> Read(string settingsPath)
    {
        var findings = new List<MailClientAccountFinding>();

        try
        {
            if (!File.Exists(settingsPath))
                return findings;

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return findings;

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (root.TryGetProperty("AccountLocalBackup", out var backup)
                && backup.ValueKind == JsonValueKind.Object
                && backup.TryGetProperty("Json", out var backupJson)
                && backupJson.ValueKind == JsonValueKind.String)
            {
                using var accounts = JsonDocument.Parse(backupJson.GetString() ?? "[]");
                if (accounts.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var account in accounts.RootElement.EnumerateArray())
                    {
                        var address = GetString(account, "emailAddress");
                        if (string.IsNullOrWhiteSpace(address) || !known.Add(address))
                            continue;

                        findings.Add(new MailClientAccountFinding
                        {
                            Source = MailClientImportSource.NewOutlook,
                            Address = address,
                            AccountName = GetString(account, "accountDescription"),
                            Kind = ToKind(GetString(account, "accountType"))
                        });
                    }
                }
            }

            // IMAP and POP accounts are listed only among the identities.
            if (root.TryGetProperty("Identities", out var identities)
                && identities.ValueKind == JsonValueKind.Object
                && identities.TryGetProperty("IdentityMap", out var identityMap)
                && identityMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var identity in identityMap.EnumerateObject())
                {
                    var address = identity.Name;
                    if (!address.Contains('@') || !known.Add(address))
                        continue;

                    findings.Add(new MailClientAccountFinding
                    {
                        Source = MailClientImportSource.NewOutlook,
                        Address = address,
                        Kind = MailClientAccountKind.Unknown
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return findings;
    }

    private static MailClientAccountKind ToKind(string accountType) => accountType.ToLowerInvariant() switch
    {
        "outlook" or "office365" or "hotmail" or "msa" or "aad" => MailClientAccountKind.Outlook,
        "gmail" or "google" => MailClientAccountKind.Gmail,
        "imap" => MailClientAccountKind.Imap,
        "pop" or "pop3" => MailClientAccountKind.Pop3,
        _ => MailClientAccountKind.Unknown
    };

    private static string GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
}
