#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wino.Platform.Apple.PushRelay;

/// <summary>
/// What the app shares with the iOS Notification Service Extension through the app's default Keychain access group.
/// The extension has no database: the directory tells it which provider and address a relay <c>cid</c> belongs to, and
/// the marker tells the app which messages the extension already showed. Scope and account names must never change.
/// </summary>
public static class RelayExtensionShared
{
    /// <summary>Keychain scope: one item per local account id ("D"), a <see cref="RelayExtensionAccount"/> JSON.</summary>
    public const string DirectoryScope = "relay-extension";

    /// <summary>Keychain scope: one item per local account id ("D"), a <see cref="RelayNotifiedMarker"/> JSON.</summary>
    public const string NotifiedScope = "relay-notified";

    /// <summary>How many message ids the marker keeps per account.</summary>
    public const int MaxNotifiedIds = 50;

    /// <summary>The App Group both bundles declare, as created in the developer portal.</summary>
    public const string AppGroupIdentifier = "group.com.winomail.macos";

    /// <summary>Folder in the group container holding one picture per sender address: <c>&lt;sha256 of the lower-cased address&gt;.img</c>.</summary>
    public const string SharedPicturesFolder = "SenderPictures";

    public static string SharedPictureFileName(string address)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(address.Trim().ToLowerInvariant()))) + ".img";

    public const string ProviderGraph = "graph";
    public const string ProviderGmail = "gmail";
    public const string ProviderImap = "imap";
}

public sealed class RelayExtensionAccount
{
    public string Provider { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? AuthenticationAddress { get; set; }

    /// <summary>Gmail: the Keychain account of the stored Google token (the account id without dashes).</summary>
    public string? CredentialKey { get; set; }
}

public sealed class RelayNotifiedMarker
{
    public List<string> Ids { get; set; } = [];
    public DateTimeOffset UpdatedUtc { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayExtensionAccount))]
[JsonSerializable(typeof(RelayNotifiedMarker))]
public sealed partial class RelayExtensionJsonContext : JsonSerializerContext;
