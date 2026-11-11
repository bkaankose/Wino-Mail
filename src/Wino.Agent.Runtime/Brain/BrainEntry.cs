using System.Text.Json.Serialization;

namespace Wino.Agent.Runtime.Brain;

/// <summary>
/// One thing the brain knows (plan section 4.8). Ids are ULIDs; <see cref="ChangedAt"/> moves on every write; a
/// delete sets <see cref="DeletedAt"/> (a tombstone, kept 90 days so sync can propagate it). A new entry with the
/// same kind, subject and value replaces the old one (same id, <see cref="ChangedAt"/> moved). The brain never holds
/// mail, event, task or contact content beyond a contact's address and display name, never a time-bound fact and
/// never free text: values are concepts, closed-vocabulary preferences or reference keys.
/// </summary>
public sealed class BrainEntry
{
    public string Id { get; set; } = "";

    public BrainEntryKind Kind { get; set; }

    public BrainSubject Subject { get; set; } = new();

    /// <summary>A concept (<c>father</c>), a <c>key=value</c> preference, an <see cref="Plan.ItemRef.Key"/>, or group member keys joined by <c>;</c>.</summary>
    public string Value { get; set; } = "";

    public BrainSource Source { get; set; }

    /// <summary>The user's words that led here, for the memory screen.</summary>
    public List<string> SaidAs { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>The device that last wrote the entry (sync ignores its own echo).</summary>
    public string? DeviceId { get; set; }

    [JsonIgnore]
    public bool IsLive => DeletedAt is null;

    [JsonIgnore]
    public IReadOnlyList<string> Members => Kind == BrainEntryKind.Group ? Value.Split(';', StringSplitOptions.RemoveEmptyEntries) : [];

    public BrainEntry Clone() => new()
    {
        Id = Id, Kind = Kind, Subject = Subject.Clone(), Value = Value, Source = Source, SaidAs = [.. SaidAs],
        CreatedAt = CreatedAt, ChangedAt = ChangedAt, DeletedAt = DeletedAt, DeviceId = DeviceId,
    };
}

public enum BrainEntryKind
{
    /// <summary>contact -> concept (<c>father</c>, <c>accountant</c>).</summary>
    PersonRelation,
    /// <summary>contact -> a name or nickname as words (<c>jay</c> for Jayesh Patel).</summary>
    PersonAlias,
    /// <summary>account -> concept or words (<c>work</c>, <c>school</c>).</summary>
    AccountNickname,
    /// <summary>self -> <c>mail=Account:..</c>, <c>task=Account:..</c>, <c>calendar=Calendar:..</c>, <c>taskList=TaskList:..</c>.</summary>
    Default,
    /// <summary>folder / calendar / list / category -> concept (<c>invoices</c>).</summary>
    ContainerSynonym,
    /// <summary>org (domain or address) -> concept (<c>bank</c>).</summary>
    SenderLabel,
    /// <summary>concept (<c>team</c>) -> member contact keys.</summary>
    Group,
    /// <summary>contact / account / self -> <c>key=value</c> (closed vocabulary).</summary>
    Preference,
    /// <summary>the user's words + kind -> the item they picked.</summary>
    Pick,
    /// <summary>words -> <c>senderKind=newsletter|service|person</c>.</summary>
    FilterWord,
    /// <summary>A declined proposal (its signature), so it is not raised again for 30 days.</summary>
    Declined,
}

public enum BrainSource
{
    Stated,
    Pick,
    Proposal,
}

public enum BrainSubjectType
{
    Contact,
    Account,
    Container,
    Org,
    Words,
    Self,
    Concept,
}

/// <summary>The thing an entry is about. Contacts carry address and display name; containers their id, type and account; words the user's words (folded).</summary>
public sealed class BrainSubject
{
    public BrainSubjectType Type { get; set; }

    public string? Address { get; set; }

    public string? Name { get; set; }

    public Guid? AccountId { get; set; }

    public Guid? ContainerId { get; set; }

    /// <summary>For containers: <see cref="Plan.ItemKind"/> name (Folder, Calendar, TaskList, Category, ContactList); for words/picks: the kind picked.</summary>
    public string? ItemKind { get; set; }

    public string? Text { get; set; }

    public BrainSubject Clone() => new() { Type = Type, Address = Address, Name = Name, AccountId = AccountId, ContainerId = ContainerId, ItemKind = ItemKind, Text = Text };

    /// <summary>The identity of the subject for replace semantics (same kind + subject key + value = same entry).</summary>
    public string Key => Type switch
    {
        BrainSubjectType.Contact => "contact:" + (Address ?? "").ToLowerInvariant(),
        BrainSubjectType.Account => "account:" + AccountId,
        BrainSubjectType.Container => $"container:{ItemKind}:{AccountId}:{ContainerId}",
        BrainSubjectType.Org => "org:" + (Address ?? "").ToLowerInvariant(),
        BrainSubjectType.Words => $"words:{ItemKind}:{Text}",
        BrainSubjectType.Self => "self",
        BrainSubjectType.Concept => "concept:" + Text,
        _ => Type.ToString(),
    };

    public static BrainSubject Contact(string address, string? name) => new() { Type = BrainSubjectType.Contact, Address = address, Name = name };
    public static BrainSubject Account(Guid id, string? name) => new() { Type = BrainSubjectType.Account, AccountId = id, Name = name };
    public static BrainSubject Container(Plan.ItemKind kind, Guid accountId, Guid id, string? name) => new() { Type = BrainSubjectType.Container, ItemKind = kind.ToString(), AccountId = accountId, ContainerId = id, Name = name };
    public static BrainSubject Org(string addressOrDomain, string? name) => new() { Type = BrainSubjectType.Org, Address = addressOrDomain, Name = name };
    public static BrainSubject Words(string folded, Plan.ItemKind? kind) => new() { Type = BrainSubjectType.Words, Text = folded, ItemKind = kind?.ToString() };
    public static BrainSubject Self() => new() { Type = BrainSubjectType.Self };
    public static BrainSubject Concept(string concept) => new() { Type = BrainSubjectType.Concept, Text = concept };

    /// <summary>The plan reference this subject names, when it names one.</summary>
    public Plan.ItemRef? ToItemRef() => Type switch
    {
        BrainSubjectType.Contact when Address is not null => Plan.ItemRef.Contact(Address),
        BrainSubjectType.Account when AccountId is { } a => Plan.ItemRef.Account(a),
        BrainSubjectType.Container when ContainerId is { } c && Enum.TryParse<Plan.ItemKind>(ItemKind, out var k) => new Plan.ItemRef(k, AccountId, c.ToString()),
        BrainSubjectType.Org when Address is not null => Plan.ItemRef.Sender(Address),
        _ => null,
    };
}

/// <summary>The brain file: one JSON document, versioned, owned by the runtime.</summary>
public sealed class BrainDocument
{
    public const int CurrentVersion = 1;

    public int V { get; set; } = CurrentVersion;

    public string DeviceId { get; set; } = "";

    public List<BrainEntry> Entries { get; set; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true,
    WriteIndented = false)]
[JsonSerializable(typeof(BrainDocument))]
public sealed partial class BrainJsonContext : JsonSerializerContext;

/// <summary>An inferred fact offered as a yes/no ("Remember that Anna is anna@example.com?"). The meaning is fixed by its fields.</summary>
public sealed record BrainProposal(Guid Id, BrainEntryKind Kind, BrainSubject Subject, string Value, string SaidAs)
{
    /// <summary>What a decline remembers: the same proposal is not raised again for 30 days.</summary>
    public string Signature => $"{Kind}|{Subject.Key}|{Value}";
}

/// <summary>ULIDs: time-ordered ids for brain entries (Crockford base32, 26 characters).</summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid(DateTimeOffset? at = null)
    {
        var time = (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[10];
        System.Security.Cryptography.RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[26];
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(time & 31)];
            time >>= 5;
        }

        // 80 random bits -> 16 characters of 5 bits.
        ulong acc = 0;
        var bits = 0;
        var pos = 10;
        foreach (var b in random)
        {
            acc = (acc << 8) | b;
            bits += 8;
            while (bits >= 5 && pos < 26)
            {
                chars[pos++] = Alphabet[(int)((acc >> (bits - 5)) & 31)];
                bits -= 5;
            }
        }

        return new string(chars);
    }
}
