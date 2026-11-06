using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Wino.Core.Synchronizers.Eas;

internal sealed class EasClient : IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _user;
    private readonly string _deviceId;
    private string? _version;
    private string _policyKey = "0";

    internal EasClient(string endpoint, string user, string password, Guid accountId)
        : this(endpoint, user, password, accountId, new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal EasClient(string endpoint, string user, string password, Guid accountId, HttpMessageHandler handler)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("An HTTPS EAS endpoint without credentials, query or fragment is required.", nameof(endpoint));

        if (string.IsNullOrWhiteSpace(user) || user.Contains(':') || string.IsNullOrEmpty(password))
            throw new ArgumentException("An EAS username and password are required.");

        _endpoint = uri.AbsolutePath is "/" or ""
            ? new Uri(uri, "/Microsoft-Server-ActiveSync")
            : uri;
        _user = user;
        _deviceId = accountId.ToString("N");
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinoMail/2.0");
    }

    internal async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, _endpoint);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        EnsureHttpSuccess(response);

        if (!response.Headers.TryGetValues("MS-ASProtocolVersions", out var advertised))
            throw new InvalidDataException("The server did not advertise EAS protocol versions.");

        var versions = advertised.SelectMany(value => value.Split(',')).Select(value => value.Trim()).ToHashSet(StringComparer.Ordinal);
        _version = new[] { "14.1", "14.0", "12.1", "12.0" }.FirstOrDefault(versions.Contains)
            ?? throw new NotSupportedException("The server advertised EAS versions: " + string.Join(", ", versions) +
                ". Wino supports EAS 12.0, 12.1, 14.0 and 14.1. Check whether the server offers a newer EAS version for this account.");
    }

    internal async Task<XElement?> CommandAsync(string command, XElement document, CancellationToken cancellationToken)
    {
        if (_version == null)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

        return await PostAsync(command, EasWbxml.Encode(document), "application/vnd.ms-sync.wbxml", cancellationToken).ConfigureAwait(false);
    }

    internal async Task SendMailAsync(byte[] mime, string clientId, CancellationToken cancellationToken)
    {
        if (_version == null)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);

        XElement? result;
        if (_version is "12.0" or "12.1")
            result = await PostAsync("SendMail&SaveInSent=T", mime, "message/rfc822", cancellationToken).ConfigureAwait(false);
        else
        {
            XNamespace compose = "ComposeMail:";
            var request = new XElement(compose + "SendMail",
                new XElement(compose + "ClientId", clientId),
                new XElement(compose + "SaveInSentItems"),
                new XElement(compose + "Mime", new XAttribute(EasWbxml.Opaque, "base64"), Convert.ToBase64String(mime)));
            result = await CommandAsync("SendMail", request, cancellationToken).ConfigureAwait(false);
        }

        if (result != null)
            RequireSuccess(result);
    }

    private async Task<XElement?> PostAsync(string command, byte[] bytes, string contentType, CancellationToken cancellationToken, bool allowProvision = true)
    {
        using var request = CreateRequest(command, bytes, contentType);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode == 449 && allowProvision)
        {
            await ProvisionAsync(cancellationToken).ConfigureAwait(false);
            return await PostAsync(command, bytes, contentType, cancellationToken, false).ConfigureAwait(false);
        }

        EnsureHttpSuccess(response);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("The EAS response exceeds the size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The EAS response exceeds the size limit.");

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
            return null;

        var result = EasWbxml.Decode(buffer.ToArray());
        if (result.DescendantsAndSelf().Any(element => element.Name.LocalName is "RemoteWipe" or "AccountOnlyRemoteWipe"))
            throw new NotSupportedException("The EAS server requested a remote wipe. No local data has been erased.");

        var status = result.Elements().FirstOrDefault(element => element.Name.LocalName == "Status")?.Value;
        if (status is "142" or "143" && allowProvision)
        {
            await ProvisionAsync(cancellationToken).ConfigureAwait(false);
            return await PostAsync(command, bytes, contentType, cancellationToken, false).ConfigureAwait(false);
        }

        return result;
    }

    private async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        XNamespace provision = "Provision:";
        XNamespace settings = "Settings:";
        var request = new XElement(provision + "Provision",
            new XElement(settings + "DeviceInformation", new XElement(settings + "Set", new XElement(settings + "Model", "Wino Mail"),
                new XElement(settings + "OS", Environment.OSVersion.VersionString), new XElement(settings + "UserAgent", "WinoMail/2.0"))),
            new XElement(provision + "Policies", new XElement(provision + "Policy", new XElement(provision + "PolicyType", "MS-EAS-Provisioning-WBXML"))));
        if (_version != "14.1")
            request.Element(settings + "DeviceInformation")?.Remove();
        var response = await PostAsync("Provision", EasWbxml.Encode(request), "application/vnd.ms-sync.wbxml", cancellationToken, false).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS provisioning response is empty.");
        RequireSuccess(response);
        var policy = response.Element(provision + "Policies")?.Element(provision + "Policy")
            ?? throw new InvalidDataException("The EAS provisioning policy is missing.");
        RequireSuccess(policy);
        var document = policy.Element(provision + "Data")?.Element(provision + "EASProvisionDoc")
            ?? throw new InvalidDataException("The EAS security policy is missing.");
        var passwordRequired = document.Element(provision + "DevicePasswordEnabled")?.Value != "0";
        var ignoredWithoutPassword = new HashSet<string>(StringComparer.Ordinal)
        { "AlphanumericDevicePasswordRequired", "PasswordRecoveryEnabled", "MinDevicePasswordLength", "MaxInactivityTimeDeviceLock",
            "MaxDevicePasswordFailedAttempts", "AllowSimpleDevicePassword", "DevicePasswordExpiration", "DevicePasswordHistory", "MinDevicePasswordComplexCharacters" };
        var allowedDefaults = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DevicePasswordEnabled"] = "0", ["RequireStorageCardEncryption"] = "0", ["RequireDeviceEncryption"] = "0",
            ["AttachmentsEnabled"] = "1", ["MaxAttachmentSize"] = "0", ["RequireManualSyncWhenRoaming"] = "0",
            ["MaxCalendarAgeFilter"] = "0", ["MaxEmailAgeFilter"] = "0", ["MaxEmailBodyTruncationSize"] = "-1",
            ["MaxEmailHTMLBodyTruncationSize"] = "-1", ["AllowHTMLEmail"] = "1", ["AllowBluetooth"] = "2",
            ["RequireSignedSMIMEMessages"] = "0", ["RequireEncryptedSMIMEMessages"] = "0"
        };
        foreach (var rule in document.Elements())
        {
            var name = rule.Name.LocalName;
            if (!passwordRequired && ignoredWithoutPassword.Contains(name))
                continue;
            if (name is "RequireSignedSMIMEAlgorithm" or "RequireEncryptionSMIMEAlgorithm" or "AllowSMIMEEncryptionAlgorithmNegotiation" or "AllowSMIMESoftCerts")
            {
                if (document.Element(provision + "RequireSignedSMIMEMessages")?.Value != "1" && document.Element(provision + "RequireEncryptedSMIMEMessages")?.Value != "1")
                    continue;
            }
            if (name is "UnapprovedInROMApplicationList" or "ApprovedApplicationList" && !rule.HasElements)
                continue;
            var allowed = allowedDefaults.TryGetValue(name, out var expected) ? rule.Value == expected
                : name.StartsWith("Allow", StringComparison.Ordinal) && rule.Value == "1";
            if (!allowed)
                throw new NotSupportedException("The EAS server requires a device security policy that Wino cannot enforce: " + name + ".");
        }
        var temporaryKey = policy.Element(provision + "PolicyKey")?.Value ?? throw new InvalidDataException("The EAS policy key is missing.");
        var acknowledgement = new XElement(provision + "Provision", new XElement(provision + "Policies", new XElement(provision + "Policy",
            new XElement(provision + "PolicyType", "MS-EAS-Provisioning-WBXML"), new XElement(provision + "PolicyKey", temporaryKey), new XElement(provision + "Status", "1"))));
        var accepted = await PostAsync("Provision", EasWbxml.Encode(acknowledgement), "application/vnd.ms-sync.wbxml", cancellationToken, false).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS provisioning acknowledgement is empty.");
        RequireSuccess(accepted);
        var acceptedPolicy = accepted.Element(provision + "Policies")?.Element(provision + "Policy")
            ?? throw new InvalidDataException("The EAS accepted policy is missing.");
        RequireSuccess(acceptedPolicy);
        _policyKey = acceptedPolicy.Element(provision + "PolicyKey")?.Value ?? throw new InvalidDataException("The EAS accepted policy key is missing.");
    }

    private HttpRequestMessage CreateRequest(string command, byte[] bytes, string contentType)
    {
        var uri = new Uri(_endpoint.AbsoluteUri + "?Cmd=" + command + "&User=" + Uri.EscapeDataString(_user) +
            "&DeviceId=" + _deviceId + "&DeviceType=WinoMail");
        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("MS-ASProtocolVersion", _version);
        request.Headers.Add("X-MS-PolicyKey", _policyKey);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return request;
    }

    internal async Task<XElement> FolderSyncAsync(string key, CancellationToken cancellationToken)
    {
        XNamespace folder = "FolderHierarchy:";
        var result = await CommandAsync("FolderSync", new XElement(folder + "FolderSync", new XElement(folder + "SyncKey", key)), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS FolderSync response was empty.");
        RequireSuccess(result);
        return result;
    }

    internal async Task<XElement> SyncAsync(string folderId, string syncKey, string itemClass, XElement? commands, CancellationToken cancellationToken)
    {
        if (_version == null)
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        XNamespace sync = "AirSync:";
        XNamespace body = "AirSyncBase:";
        var collection = new XElement(sync + "Collection",
            new XElement(sync + "SyncKey", syncKey),
            new XElement(sync + "CollectionId", folderId));
        if (_version == "12.0")
            collection.AddFirst(new XElement(sync + "Class", itemClass));
        if (syncKey == "0" && itemClass == "Contacts")
            collection.Add(new XElement(sync + "Supported"));
        if (syncKey != "0")
        {
            collection.Add(new XElement(sync + "DeletesAsMoves", "1"),
                new XElement(sync + "GetChanges", "1"),
                new XElement(sync + "WindowSize", "100"),
                new XElement(sync + "Options",
                    new XElement(sync + "Conflict", "0"),
                    new XElement(body + "BodyPreference", new XElement(body + "Type", itemClass == "Email" ? "4" : "1"))));
            if (itemClass == "Email")
                collection.Element(sync + "Options")!.Add(new XElement(sync + "MIMESupport", "2"), new XElement(sync + "MIMETruncation", "8"));
        }

        if (commands != null)
            collection.Add(commands);

        var response = await CommandAsync("Sync", new XElement(sync + "Sync", new XElement(sync + "Collections", collection)), cancellationToken).ConfigureAwait(false);
        if (response == null && commands == null && syncKey != "0")
            return new XElement(sync + "Collection", new XElement(sync + "Status", "1"), new XElement(sync + "SyncKey", syncKey));
        if (response == null)
            throw new InvalidDataException("The EAS Sync response was empty.");
        if (response.Element(sync + "Status") != null)
            RequireSuccess(response);
        var result = response.Element(sync + "Collections")?.Element(sync + "Collection")
            ?? throw new InvalidDataException("The EAS Sync response has no collection.");
        RequireSuccess(result);
        foreach (var operation in result.Element(sync + "Responses")?.Elements() ?? Enumerable.Empty<XElement>())
            RequireSuccess(operation);

        return result;
    }

    internal async Task<XElement> FetchAsync(string folderId, string serverId, CancellationToken cancellationToken)
    {
        XNamespace items = "ItemOperations:";
        XNamespace sync = "AirSync:";
        XNamespace body = "AirSyncBase:";
        var response = await CommandAsync("ItemOperations", new XElement(items + "ItemOperations",
            new XElement(items + "Fetch", new XElement(items + "Store", "Mailbox"),
                new XElement(sync + "CollectionId", folderId), new XElement(sync + "ServerId", serverId),
                new XElement(items + "Options", new XElement(body + "BodyPreference", new XElement(body + "Type", "4")),
                    new XElement(sync + "MIMESupport", "2")))), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The EAS item fetch response was empty.");
        RequireSuccess(response);
        var fetch = response.Element(items + "Response")?.Element(items + "Fetch")
            ?? throw new InvalidDataException("The EAS item fetch response has no item.");
        RequireSuccess(fetch);
        return fetch.Element(items + "Properties") ?? throw new InvalidDataException("The EAS item has no properties.");
    }

    internal static void RequireSuccess(XElement response)
    {
        var status = response.Elements().FirstOrDefault(element => element.Name.LocalName == "Status")?.Value;
        if (status != "1")
            throw new EasCommandException(response.Name.LocalName, status ?? "missing");
    }

    private static void EnsureHttpSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("EAS authentication was rejected. Check the credentials and whether Basic authentication is permitted.", null, response.StatusCode);

        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("The EAS server redirected the request. Configure its final HTTPS endpoint; credentials were not forwarded.", null, response.StatusCode);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("EAS returned HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}

internal sealed class EasCommandException(string command, string status)
    : IOException("EAS " + command + " failed with status " + status + ".")
{
    internal string Status { get; } = status;
}
