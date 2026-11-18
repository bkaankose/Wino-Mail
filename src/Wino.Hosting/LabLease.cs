using System.Text.Json;
using Wino.Core.Domain.Enums;

namespace Wino.Hosting;

/// <summary>
/// A lab lease file that <c>scripts/lab/windows/lab.ps1 lease</c> wrote: its IMAP accounts and, when leased with
/// <c>-Pop3</c>, its POP3 accounts.
/// The password stays in memory: <see cref="ToString"/> and the account's <see cref="LabLeaseAccount.ToString"/> leave it out.
/// </summary>
public sealed class LabLease
{
    private LabLease(string id, IReadOnlyList<LabLeaseAccount> accounts, IReadOnlyList<LabLeaseAccount> pop3Accounts)
    {
        Id = id;
        Accounts = accounts;
        Pop3Accounts = pop3Accounts;
    }

    public string Id { get; }

    public IReadOnlyList<LabLeaseAccount> Accounts { get; }

    /// <summary>The POP3 accounts; <see cref="LabLeaseAccount.IsPop3"/> is true and the incoming server fields hold POP3.</summary>
    public IReadOnlyList<LabLeaseAccount> Pop3Accounts { get; }

    /// <summary>The account on the given lab server key (dovecot, stalwart, …, or pop3).</summary>
    public LabLeaseAccount Account(string serverKey)
        => Accounts.Concat(Pop3Accounts).FirstOrDefault(account => string.Equals(account.Key, serverKey, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"The lease {Id} has no account on '{serverKey}'.");

    public static LabLease Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var leasePassword = ReadString(root, "password");
        var accounts = new List<LabLeaseAccount>();

        if (root.TryGetProperty("accounts", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
                accounts.Add(LabLeaseAccount.Read(item, leasePassword));
        }

        var pop3Accounts = new List<LabLeaseAccount>();
        if (root.TryGetProperty("pop3Accounts", out var pop3Items) && pop3Items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pop3Items.EnumerateArray())
                pop3Accounts.Add(LabLeaseAccount.ReadPop3(item, leasePassword));
        }

        return new LabLease(ReadString(root, "id"), accounts, pop3Accounts);
    }

    public override string ToString() => $"Lease {Id}: {string.Join(", ", Accounts.Concat(Pop3Accounts).Select(account => account.Address))}";

    internal static string ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    internal static int ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}

public sealed class LabLeaseAccount
{
    public string Key { get; private init; } = string.Empty;
    public string Name { get; private init; } = string.Empty;
    public string Address { get; private init; } = string.Empty;
    public string ImapHost { get; private init; } = string.Empty;
    public int ImapPort { get; private init; }
    public ImapConnectionSecurity ImapSecurity { get; private init; }
    public ImapAuthenticationMethod ImapAuthentication { get; private init; }
    public string SmtpHost { get; private init; } = string.Empty;
    public int SmtpPort { get; private init; }
    public ImapConnectionSecurity SmtpSecurity { get; private init; }
    public ImapAuthenticationMethod SmtpAuthentication { get; private init; }

    /// <summary>The incoming server is POP3: <see cref="ImapHost"/> and <see cref="ImapPort"/> name the POP3 server.</summary>
    public bool IsPop3 { get; private init; }

    /// <summary>The CalDAV service address, or empty when the server has no DAV.</summary>
    public string DavUrl { get; private init; } = string.Empty;
    public string CardDavUrl { get; private init; } = string.Empty;
    public string DavUsername { get; private init; } = string.Empty;

    /// <summary>Message count per folder of the baseline seed the lease was reset to.</summary>
    public IReadOnlyDictionary<string, int> SeedFolderCounts { get; private init; } = new Dictionary<string, int>();

    /// <summary>Never print or log this value.</summary>
    public string Password { get; private init; } = string.Empty;

    public override string ToString() => $"{Address} ({(IsPop3 ? "POP3" : "IMAP")} {ImapHost}:{ImapPort}, SMTP {SmtpHost}:{SmtpPort})";

    internal static LabLeaseAccount Read(JsonElement item, string leasePassword)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("perFolder", out var perFolder) && perFolder.ValueKind == JsonValueKind.Object)
        {
            foreach (var folder in perFolder.EnumerateObject())
            {
                if (folder.Value.ValueKind == JsonValueKind.Number)
                    counts[folder.Name] = folder.Value.GetInt32();
            }
        }

        var password = LabLease.ReadString(item, "password");

        return new LabLeaseAccount
        {
            Key = LabLease.ReadString(item, "key"),
            Name = LabLease.ReadString(item, "name"),
            Address = LabLease.ReadString(item, "address"),
            ImapHost = LabLease.ReadString(item, "imapHost"),
            ImapPort = LabLease.ReadInt(item, "imapPort"),
            ImapSecurity = ParseEnum(LabLease.ReadString(item, "imapSecurity"), ImapConnectionSecurity.SslTls),
            ImapAuthentication = ParseEnum(LabLease.ReadString(item, "imapAuth"), ImapAuthenticationMethod.NormalPassword),
            SmtpHost = LabLease.ReadString(item, "smtpHost"),
            SmtpPort = LabLease.ReadInt(item, "smtpPort"),
            SmtpSecurity = ParseEnum(LabLease.ReadString(item, "smtpSecurity"), ImapConnectionSecurity.SslTls),
            SmtpAuthentication = ParseEnum(LabLease.ReadString(item, "smtpAuth"), ImapAuthenticationMethod.NormalPassword),
            SeedFolderCounts = counts,
            DavUrl = LabLease.ReadString(item, "davUrl"),
            CardDavUrl = LabLease.ReadString(item, "cardDavUrl"),
            DavUsername = LabLease.ReadString(item, "davUsername"),
            Password = string.IsNullOrEmpty(password) ? leasePassword : password
        };
    }

    internal static LabLeaseAccount ReadPop3(JsonElement item, string leasePassword)
    {
        var password = LabLease.ReadString(item, "password");
        var address = LabLease.ReadString(item, "address");

        return new LabLeaseAccount
        {
            Key = LabLease.ReadString(item, "key"),
            Name = LabLease.ReadString(item, "name"),
            Address = address,
            IsPop3 = true,
            ImapHost = LabLease.ReadString(item, "pop3Host"),
            ImapPort = LabLease.ReadInt(item, "pop3Port"),
            ImapSecurity = ParseEnum(LabLease.ReadString(item, "pop3Security"), ImapConnectionSecurity.SslTls),
            ImapAuthentication = ImapAuthenticationMethod.NormalPassword,
            SmtpHost = LabLease.ReadString(item, "smtpHost"),
            SmtpPort = LabLease.ReadInt(item, "smtpPort"),
            SmtpSecurity = ParseEnum(LabLease.ReadString(item, "smtpSecurity"), ImapConnectionSecurity.SslTls),
            SmtpAuthentication = ParseEnum(LabLease.ReadString(item, "smtpAuth"), ImapAuthenticationMethod.NormalPassword),
            Password = string.IsNullOrEmpty(password) ? leasePassword : password
        };
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
