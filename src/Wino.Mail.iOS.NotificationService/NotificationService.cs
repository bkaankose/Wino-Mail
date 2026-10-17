#nullable enable
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foundation;
using Microsoft.Identity.Client;
using ObjCRuntime;
using Security;
using UserNotifications;

namespace Wino.Mail.iOS.NotificationService;

/// <summary>
/// Rewrites the relay's "New mail" alert with the real sender, subject and preview. Outlook goes through MSAL's shared
/// Keychain cache and Graph; Gmail uses the app's stored access token; anything else keeps the relay's text. Whatever
/// the extension shows is recorded in the shared <c>relay-notified</c> Keychain item, so the app does not notify again.
/// Any failure or timeout delivers the original alert unchanged.
/// </summary>
[Register("NotificationService")]
public sealed class NotificationService : UNNotificationServiceExtension
{
    private const string AppBundleId = "com.winomail.macos";
    private const string OutlookClientId = "b19c2035-d740-49ff-b297-de6ec561b208";
    private const string MsalKeychainGroup = "com.microsoft.adalcache";
    private const string DirectoryScope = "relay-extension";
    private const string NotifiedScope = "relay-notified";
    private const string GoogleTokenScope = "google-tokens";
    private const int MaxNotifiedIds = 50;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private Action<UNNotificationContent>? _contentHandler;
    private UNMutableNotificationContent? _bestAttempt;
    private CancellationTokenSource? _cancellation;

    protected NotificationService(NativeHandle handle) : base(handle)
    {
    }

    public override void DidReceiveNotificationRequest(UNNotificationRequest request, Action<UNNotificationContent> contentHandler)
    {
        _contentHandler = contentHandler;
        Trace("received");
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            // The original request's user info is the relay payload; the mutable copy only carries the text we change.
            payload = ReadPayload(request.Content.UserInfo);
            Trace($"userinfo {payload.Count} keys");
            var copy = request.Content.MutableCopy();
            _bestAttempt = copy as UNMutableNotificationContent ?? Runtime.GetNSObject<UNMutableNotificationContent>(copy.Handle);
            Trace(_bestAttempt is null ? "no mutable copy" : "copied");
        }
        catch (Exception ex)
        {
            Trace($"setup error {ex.GetType().Name}: {Shorten(ex.Message)}");
            _bestAttempt = null;
        }

        if (_bestAttempt is null)
        {
            Interlocked.Exchange(ref _contentHandler, null)?.Invoke(request.Content);
            return;
        }

        _cancellation = new CancellationTokenSource(Budget);
        _ = EnrichAsync(_bestAttempt, payload, _cancellation.Token);
    }

    public override void TimeWillExpire()
    {
        Trace("expired");
        Deliver();
    }

    private void Deliver()
    {
        var handler = Interlocked.Exchange(ref _contentHandler, null);
        if (handler is null || _bestAttempt is null) return;
        _cancellation?.Cancel();
        handler(_bestAttempt);
    }

    private async Task EnrichAsync(UNMutableNotificationContent content, Dictionary<string, string> payload, CancellationToken cancellationToken)
    {
        try
        {
            Trace($"payload t={payload.GetValueOrDefault("t")} cid={payload.GetValueOrDefault("cid")} msg={(payload.ContainsKey("msg") ? "yes" : "no")}");
            if (payload.GetValueOrDefault("t") != "mailbox.changed" || payload.GetValueOrDefault("cid") is not { Length: > 0 } accountId)
                return;

            var account = ReadJson(DirectoryScope, accountId, ExtensionJsonContext.Default.ExtensionAccount);
            Trace(account is null ? "directory miss" : $"directory {account.Provider}");
            if (account is null) return;

            var message = account.Provider switch
            {
                "graph" => await FetchGraphAsync(account, payload.GetValueOrDefault("msg"), cancellationToken).ConfigureAwait(false),
                "gmail" => await FetchGmailAsync(account, cancellationToken).ConfigureAwait(false),
                _ => null
            };
            Trace(message is null ? "no message" : "enriched");
            if (message is null) return;

            content.Title = message.Sender;
            content.Subtitle = message.Subject;
            content.Body = message.Preview;
            content.ThreadIdentifier = accountId;
            Trace("content set");

            var info = content.UserInfo is { } existing ? new NSMutableDictionary(existing) : new NSMutableDictionary();
            info[new NSString("wino.mailId")] = new NSString(message.Id);
            info[new NSString("wino.accountId")] = new NSString(accountId);
            if (!string.IsNullOrEmpty(message.InternetMessageId))
                info[new NSString("wino.messageId")] = new NSString(message.InternetMessageId);
            content.UserInfo = info;

            RememberNotified(accountId, message.Id);
            Trace("marker written");

            // Same picture the app would show: Gravatar first, then the sender domain's favicon. Local contact photos
            // live in the app's container, which the extension cannot read without an App Group.
            var avatarPath = await FetchAvatarAsync(message.SenderAddress, cancellationToken).ConfigureAwait(false);
            if (avatarPath is not null)
            {
                var attachment = UNNotificationAttachment.FromIdentifier("avatar", NSUrl.FromFilename(avatarPath), new UNNotificationAttachmentOptions(), out var attachmentError);
                if (attachment is not null) content.Attachments = [attachment];
                Trace(attachment is not null ? "avatar attached" : $"avatar not attached: {attachmentError?.LocalizedDescription}");
            }
            else
            {
                Trace("no avatar");
            }
        }
        catch (Exception ex)
        {
            // The original alert is still correct, only less specific.
            Trace($"error {ex.GetType().Name}: {Shorten(ex.Message)}");
        }
        finally
        {
            Deliver();
        }
    }

    private static Dictionary<string, string> ReadPayload(NSDictionary? userInfo)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userInfo is null) return payload;
        foreach (var key in userInfo.Keys)
        {
            if (key is not NSString name || name.ToString() == "aps") continue;
            if (userInfo[key] is NSString text) payload[name.ToString()] = text.ToString();
        }
        return payload;
    }

    private static async Task<EnrichedMessage?> FetchGraphAsync(ExtensionAccount account, string? messageId, CancellationToken cancellationToken)
    {
        var client = PublicClientApplicationBuilder.Create(OutlookClientId)
            .WithRedirectUri($"msauth.{AppBundleId}://auth")
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithIosKeychainSecurityGroup(MsalKeychainGroup)
            .Build();

        var accounts = (await client.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).ToList();
        Trace($"msal accounts {accounts.Count}");
        var wanted = account.AuthenticationAddress ?? account.Address;
        var msalAccount = accounts.FirstOrDefault(a => string.Equals(a.Username?.Trim(), wanted?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? accounts.FirstOrDefault();
        if (msalAccount is null) return null;

        // The scope the app signed in with; a subset would force a refresh round trip inside the budget.
        var token = await client.AcquireTokenSilent(["Mail.ReadWrite"], msalAccount).ExecuteAsync(cancellationToken).ConfigureAwait(false);
        Trace("graph token ok");

        var uri = string.IsNullOrEmpty(messageId)
            ? "https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?$top=1&$orderby=receivedDateTime%20desc&$select=id,subject,from,bodyPreview,internetMessageId"
            : $"https://graph.microsoft.com/v1.0/me/messages/{Uri.EscapeDataString(messageId)}?$select=id,subject,from,bodyPreview,internetMessageId";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Trace($"graph http {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        GraphMessage? graphMessage;
        if (string.IsNullOrEmpty(messageId))
        {
            var page = await JsonSerializer.DeserializeAsync(stream, ExtensionJsonContext.Default.GraphMessagePage, cancellationToken).ConfigureAwait(false);
            graphMessage = page?.Value?.FirstOrDefault();
        }
        else
        {
            graphMessage = await JsonSerializer.DeserializeAsync(stream, ExtensionJsonContext.Default.GraphMessage, cancellationToken).ConfigureAwait(false);
        }

        if (graphMessage?.Id is null) return null;
        var sender = graphMessage.From?.EmailAddress;
        return new EnrichedMessage(graphMessage.Id,
            FirstNonEmpty(sender?.Name, sender?.Address, "New mail"),
            graphMessage.Subject ?? string.Empty,
            Trim(graphMessage.BodyPreview),
            sender?.Address,
            graphMessage.InternetMessageId);
    }

    private static async Task<EnrichedMessage?> FetchGmailAsync(ExtensionAccount account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(account.CredentialKey)) return null;
        var token = ReadJson(GoogleTokenScope, account.CredentialKey, ExtensionJsonContext.Default.StoredGoogleToken);
        // The extension cannot refresh (no client secret); a stale token leaves the relay's text in place.
        if (token is null || string.IsNullOrEmpty(token.AccessToken) || token.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1)) return null;

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/messages?q=in%3Ainbox&maxResults=1");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var listResponse = await Http.SendAsync(listRequest, cancellationToken).ConfigureAwait(false);
        if (!listResponse.IsSuccessStatusCode) return null;
        var list = await JsonSerializer.DeserializeAsync(await listResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            ExtensionJsonContext.Default.GmailMessageList, cancellationToken).ConfigureAwait(false);
        var id = list?.Messages?.FirstOrDefault()?.Id;
        if (string.IsNullOrEmpty(id)) return null;

        using var getRequest = new HttpRequestMessage(HttpMethod.Get,
            $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From&metadataHeaders=Subject&metadataHeaders=Message-ID");
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var getResponse = await Http.SendAsync(getRequest, cancellationToken).ConfigureAwait(false);
        if (!getResponse.IsSuccessStatusCode) return null;
        var message = await JsonSerializer.DeserializeAsync(await getResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            ExtensionJsonContext.Default.GmailMessage, cancellationToken).ConfigureAwait(false);
        if (message?.Id is null) return null;

        var headers = message.Payload?.Headers ?? [];
        var from = headers.FirstOrDefault(h => string.Equals(h.Name, "From", StringComparison.OrdinalIgnoreCase))?.Value;
        var subject = headers.FirstOrDefault(h => string.Equals(h.Name, "Subject", StringComparison.OrdinalIgnoreCase))?.Value;
        var messageId = headers.FirstOrDefault(h => string.Equals(h.Name, "Message-ID", StringComparison.OrdinalIgnoreCase))?.Value;
        return new EnrichedMessage(message.Id, FirstNonEmpty(DisplayName(from), from, "New mail"), subject ?? string.Empty, Trim(message.Snippet), AddressOf(from), messageId);
    }

    private static void RememberNotified(string accountId, string messageId)
    {
        var marker = ReadJson(NotifiedScope, accountId, ExtensionJsonContext.Default.NotifiedMarker) ?? new NotifiedMarker();
        marker.Ids.Remove(messageId);
        marker.Ids.Insert(0, messageId);
        if (marker.Ids.Count > MaxNotifiedIds) marker.Ids.RemoveRange(MaxNotifiedIds, marker.Ids.Count - MaxNotifiedIds);
        marker.UpdatedUtc = DateTimeOffset.UtcNow;
        WriteKeychain(NotifiedScope, accountId, JsonSerializer.SerializeToUtf8Bytes(marker, ExtensionJsonContext.Default.NotifiedMarker));
    }

    // Keychain items the app wrote through AppleKeychainStore: service "<bundle id>.v1.<scope>", default access group.
    private static string Service(string scope) => $"{AppBundleId}.v1.{scope}";

    private static T? ReadJson<T>(string scope, string account, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) where T : class
    {
        using var query = new SecRecord(SecKind.GenericPassword) { Service = Service(scope), Account = account };
        using var data = SecKeyChain.QueryAsData(query, false, out var status);
        if (status != SecStatusCode.Success || data is null) return null;
        try { return JsonSerializer.Deserialize(data.ToArray(), typeInfo); }
        catch (JsonException) { return null; }
    }

    private static void WriteKeychain(string scope, string account, byte[] bytes)
    {
        using var query = new SecRecord(SecKind.GenericPassword) { Service = Service(scope), Account = account };
        using var value = NSData.FromArray(bytes);
        using var update = new SecRecord { ValueData = value };
        var status = SecKeyChain.Update(query, update);
        if (status == SecStatusCode.ItemNotFound)
        {
            using var record = new SecRecord(SecKind.GenericPassword)
            {
                Service = Service(scope),
                Account = account,
                ValueData = value,
                Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly
            };
            SecKeyChain.Add(record);
        }
    }

    private const int AvatarPixelSize = 96;
    private static readonly string[] ExcludedFaviconDomains = ["gmail.com", "outlook.com", "hotmail.com", "live.com", "yahoo.com", "icloud.com", "aol.com", "protonmail.com", "zoho.com", "mail.com", "gmx.com", "yandex.com", "yandex.ru", "tutanota.com", "mail.ru", "rediffmail.com"];

    private const string AppGroupIdentifier = "group.com.winomail.macos";
    private const string SharedPicturesFolder = "SenderPictures";

    /// <summary>
    /// The synced contact picture the app mirrored into the App Group (keyed by the sender address), then Gravatar
    /// (MD5 of the lower-cased address, 404 when none), then the Google favicon of the sender's domain, as the app does.
    /// </summary>
    private static async Task<string?> FetchAvatarAsync(string? address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address) || !address.Contains('@')) return null;
        var normalized = address.Trim().ToLowerInvariant();

        var shared = SharedContactPicture(normalized);
        if (shared is not null) return shared;

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        var avatar = await DownloadImageAsync($"https://www.gravatar.com/avatar/{hash}?s={AvatarPixelSize}&d=404", cancellationToken).ConfigureAwait(false);
        if (avatar is not null) return avatar;

        var host = normalized[(normalized.LastIndexOf('@') + 1)..];
        if (host.Length == 0 || ExcludedFaviconDomains.Contains(host, StringComparer.OrdinalIgnoreCase)) return null;
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var primaryDomain = parts.Length > 1 ? string.Join('.', parts[^2..]) : host;
        return await DownloadImageAsync($"https://www.google.com/s2/favicons?sz={AvatarPixelSize}&domain_url={Uri.EscapeDataString(primaryDomain)}", cancellationToken).ConfigureAwait(false);
    }

    private static string? SharedContactPicture(string normalizedAddress)
    {
        try
        {
            using var container = NSFileManager.DefaultManager.GetContainerUrl(AppGroupIdentifier);
            if (container?.Path is not { } root) { Trace("no group container"); return null; }
            var name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedAddress))) + ".img";
            var source = Path.Combine(root, SharedPicturesFolder, name);
            if (!File.Exists(source)) return null;
            // The notification center moves the attachment into its own store, so hand it a copy.
            var copy = Path.Combine(NSFileManager.DefaultManager.GetTemporaryDirectory().Path!, $"wino-avatar-{Guid.NewGuid():N}.jpg");
            File.Copy(source, copy, overwrite: true);
            return copy;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> DownloadImageAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 64) return null;
            var extension = response.Content.Headers.ContentType?.MediaType switch
            {
                "image/png" => ".png",
                "image/gif" => ".gif",
                _ => ".jpg"
            };
            var path = Path.Combine(NSFileManager.DefaultManager.GetTemporaryDirectory().Path!, $"wino-avatar-{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"Name &lt;address&gt;" or a bare address becomes the address.</summary>
    private static string? AddressOf(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;
        var lt = from.IndexOf('<'); var gt = from.IndexOf('>');
        if (lt >= 0 && gt > lt) return from[(lt + 1)..gt].Trim();
        return from.Contains('@') ? from.Trim() : null;
    }

    private const string TraceScope = "relay-extension-log";
    private static readonly object TraceLock = new();

    /// <summary>A short rolling trace in the shared Keychain; the app writes it to its log at launch. Never holds tokens or mail content.</summary>
    private static void Trace(string line)
    {
        try
        {
            lock (TraceLock)
            {
                var existing = ReadKeychainText(TraceScope, "trace") ?? string.Empty;
                var text = $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} {line}\n{existing}";
                if (text.Length > 3000) text = text[..3000];
                WriteKeychain(TraceScope, "trace", System.Text.Encoding.UTF8.GetBytes(text));
            }
        }
        catch (Exception)
        {
            // Tracing must never affect the notification.
        }
    }

    private static string? ReadKeychainText(string scope, string account)
    {
        using var query = new SecRecord(SecKind.GenericPassword) { Service = Service(scope), Account = account };
        using var data = SecKeyChain.QueryAsData(query, false, out var status);
        return status == SecStatusCode.Success && data is not null ? System.Text.Encoding.UTF8.GetString(data.ToArray()) : null;
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160];

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var compact = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180 ? compact : compact[..180] + "…";
    }

    /// <summary>"Name &lt;address&gt;" becomes "Name".</summary>
    private static string? DisplayName(string? from)
    {
        if (string.IsNullOrWhiteSpace(from)) return null;
        var lt = from.IndexOf('<');
        var name = (lt > 0 ? from[..lt] : from).Trim().Trim('"');
        return string.IsNullOrEmpty(name) ? null : name;
    }
}

internal sealed record EnrichedMessage(string Id, string Sender, string Subject, string Preview, string? SenderAddress, string? InternetMessageId = null);

internal sealed class ExtensionAccount
{
    public string Provider { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string? AuthenticationAddress { get; set; }
    public string? CredentialKey { get; set; }
}

internal sealed class NotifiedMarker
{
    public List<string> Ids { get; set; } = [];
    public DateTimeOffset UpdatedUtc { get; set; }
}

internal sealed class StoredGoogleToken
{
    public string AccessToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

internal sealed class GraphMessagePage
{
    [JsonPropertyName("value")] public List<GraphMessage>? Value { get; set; }
}

internal sealed class GraphMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("subject")] public string? Subject { get; set; }
    [JsonPropertyName("bodyPreview")] public string? BodyPreview { get; set; }
    [JsonPropertyName("internetMessageId")] public string? InternetMessageId { get; set; }
    [JsonPropertyName("from")] public GraphRecipient? From { get; set; }
}

internal sealed class GraphRecipient
{
    [JsonPropertyName("emailAddress")] public GraphEmailAddress? EmailAddress { get; set; }
}

internal sealed class GraphEmailAddress
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }
}

internal sealed class GmailMessageList
{
    [JsonPropertyName("messages")] public List<GmailMessageRef>? Messages { get; set; }
}

internal sealed class GmailMessageRef
{
    [JsonPropertyName("id")] public string? Id { get; set; }
}

internal sealed class GmailMessage
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("snippet")] public string? Snippet { get; set; }
    [JsonPropertyName("payload")] public GmailPayload? Payload { get; set; }
}

internal sealed class GmailPayload
{
    [JsonPropertyName("headers")] public List<GmailHeader>? Headers { get; set; }
}

internal sealed class GmailHeader
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExtensionAccount))]
[JsonSerializable(typeof(NotifiedMarker))]
[JsonSerializable(typeof(StoredGoogleToken))]
[JsonSerializable(typeof(GraphMessagePage))]
[JsonSerializable(typeof(GraphMessage))]
[JsonSerializable(typeof(GmailMessageList))]
[JsonSerializable(typeof(GmailMessage))]
internal sealed partial class ExtensionJsonContext : JsonSerializerContext;
