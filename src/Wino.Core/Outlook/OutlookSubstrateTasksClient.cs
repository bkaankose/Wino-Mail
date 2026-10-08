using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Outlook;

// Substrate uses PascalCase and occasionally sends numbers as strings. Source generated so the
// client stays trim and AOT safe, matching GoogleTasksClient.
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(SubstrateCollection<SubstrateFolderGroup>))]
[JsonSerializable(typeof(SubstrateCollection<SubstrateTaskFolder>))]
[JsonSerializable(typeof(SubstrateFolderGroup))]
[JsonSerializable(typeof(SubstrateTaskFolder))]
[JsonSerializable(typeof(SubstrateFolderGroupMutation))]
[JsonSerializable(typeof(SubstrateTaskFolderMutation))]
internal partial class SubstrateTasksJsonContext : JsonSerializerContext;

/// <summary>
/// Reader for the undocumented To Do substrate API that the Microsoft To Do clients use.
///
/// Graph owns lists and tasks; this only supplies what Graph cannot express at all — the
/// folder groups, each folder's parent group, its theme colour, and its per-list sort. The
/// folder ids it returns are byte-identical to the Graph <c>todoTaskList</c> ids and its
/// ChangeKey equals the Graph etag, so the two compose without any correlation step.
///
/// There is no contract behind this API. Every call is best effort: a non-success status or a
/// changed payload shape must leave task synchronization untouched.
/// </summary>
public sealed class OutlookSubstrateTasksClient
{
    // The clients call substrate.office.com. The same API answers on the Outlook host, which is
    // the one an ordinary app registration's Exchange Online token is scoped to, so it goes first.
    private static readonly string[] Hosts =
    [
        "https://outlook.office.com",
        "https://substrate.office.com"
    ];

    private readonly HttpClient _httpClient;
    private readonly Func<Task<string>> _tokenProvider;

    public OutlookSubstrateTasksClient(HttpClient httpClient, Func<Task<string>> tokenProvider)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
    }

    public Task<SubstrateCollection<SubstrateFolderGroup>> GetFolderGroupsAsync(
        string url = null, CancellationToken cancellationToken = default)
        => GetAsync<SubstrateFolderGroup>(url, "/todob2/api/v1/foldergroups", cancellationToken);

    public Task<SubstrateCollection<SubstrateTaskFolder>> GetTaskFoldersAsync(
        string url = null, CancellationToken cancellationToken = default)
        => GetAsync<SubstrateTaskFolder>(url, "/todob2/api/v1/taskfolders", cancellationToken);

    public Task<SubstrateFolderGroup> CreateFolderGroupAsync(
        string name, DateTimeOffset orderDateTime, CancellationToken cancellationToken = default)
        => SendAsync<SubstrateFolderGroup, SubstrateFolderGroupMutation>(
            HttpMethod.Post,
            "/todob2/api/v1/foldergroups",
            new SubstrateFolderGroupMutation { Name = name, OrderDateTime = orderDateTime },
            null,
            cancellationToken);

    public Task<SubstrateFolderGroup> UpdateFolderGroupAsync(
        string id, string name, DateTimeOffset? orderDateTime, string changeKey, CancellationToken cancellationToken = default)
        => SendAsync<SubstrateFolderGroup, SubstrateFolderGroupMutation>(
            HttpMethod.Patch,
            $"/todob2/api/v1/foldergroups/{Uri.EscapeDataString(id)}",
            new SubstrateFolderGroupMutation { Name = name, OrderDateTime = orderDateTime },
            changeKey,
            cancellationToken);

    public Task DeleteFolderGroupAsync(string id, string changeKey, CancellationToken cancellationToken = default)
        => SendNoContentAsync(
            $"/todob2/api/v1/foldergroups/{Uri.EscapeDataString(id)}", changeKey, cancellationToken);

    public Task<SubstrateTaskFolder> UpdateTaskFolderAsync(
        string id, string parentFolderGroupId, DateTimeOffset orderDateTime, string changeKey, CancellationToken cancellationToken = default)
        => SendAsync<SubstrateTaskFolder, SubstrateTaskFolderMutation>(
            HttpMethod.Patch,
            $"/todob2/api/v1/taskfolders/{Uri.EscapeDataString(id)}",
            new SubstrateTaskFolderMutation
            {
                ParentFolderGroupId = parentFolderGroupId,
                OrderDateTime = orderDateTime
            },
            changeKey,
            cancellationToken);

    /// <summary>
    /// Returns null rather than throwing on any failure. A delta link is absolute, so when one is
    /// supplied it is used as-is and the host fallback does not apply.
    /// </summary>
    private async Task<SubstrateCollection<T>> GetAsync<T>(
        string url, string path, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var candidates = string.IsNullOrWhiteSpace(url)
            ? Array.ConvertAll(Hosts, host => host + path)
            : [url];

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = CreateRequest(HttpMethod.Get, candidate, token);

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(url) &&
                    response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    throw new SubstrateDeltaCursorInvalidException(response.StatusCode);
                }
                if (!response.IsSuccessStatusCode)
                    continue;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return (SubstrateCollection<T>)await JsonSerializer
                    .DeserializeAsync(stream, typeof(SubstrateCollection<T>), SubstrateTasksJsonContext.Default, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not SubstrateDeltaCursorInvalidException &&
                                              (exception is HttpRequestException or JsonException or TaskCanceledException) &&
                                              !cancellationToken.IsCancellationRequested)
            {
                // Try the next host, then give up and let the caller fall back to local grouping.
            }
        }

        return null;
    }

    private async Task<TResponse> SendAsync<TResponse, TBody>(
        HttpMethod method, string path, TBody body, string changeKey, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Substrate task authorization is unavailable.");

        // Writes use one host only. Retrying an ambiguous POST against another host can create
        // a duplicate group; the subsequent delta pass is the recovery mechanism.
        using var request = CreateRequest(method, Hosts[0] + path, token, changeKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, typeof(TBody), SubstrateTasksJsonContext.Default),
            Encoding.UTF8,
            "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Substrate task request failed with {(int)response.StatusCode} ({response.ReasonPhrase}). {error}",
                null,
                response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return (TResponse)await JsonSerializer.DeserializeAsync(
            stream, typeof(TResponse), SubstrateTasksJsonContext.Default, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendNoContentAsync(string path, string changeKey, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Substrate task authorization is unavailable.");

        using var request = CreateRequest(HttpMethod.Delete, Hosts[0] + path, token, changeKey);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string token, string changeKey = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize=200");
        request.Headers.TryAddWithoutValidation("X-Todo-Request-Id", Guid.NewGuid().ToString("D"));
        if (!string.IsNullOrWhiteSpace(changeKey))
            request.Headers.TryAddWithoutValidation("If-Match", changeKey);
        if (TryGetAnchorMailbox(token, out var anchorMailbox))
            request.Headers.TryAddWithoutValidation("X-AnchorMailbox", anchorMailbox);
        return request;
    }

    private static bool TryGetAnchorMailbox(string token, out string anchorMailbox)
    {
        anchorMailbox = null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
                return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = document.RootElement;
            if (root.TryGetProperty("oid", out var oid) && root.TryGetProperty("tid", out var tid))
            {
                anchorMailbox = $"OID:{oid.GetString()}@{tid.GetString()}";
                return true;
            }
            if (root.TryGetProperty("cid", out var cid))
            {
                anchorMailbox = $"CID:{cid.GetString()}";
                return true;
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
        }
        return false;
    }
}

public sealed class SubstrateDeltaCursorInvalidException : HttpRequestException
{
    public SubstrateDeltaCursorInvalidException(HttpStatusCode statusCode)
        : base("The Substrate delta cursor is no longer valid.", null, statusCode)
    {
    }
}

public sealed class SubstrateCollection<T>
{
    public string DeltaLink { get; set; }
    public string NextLink { get; set; }
    public List<T> Value { get; set; } = [];
}

public sealed class SubstrateFolderGroup
{
    public string Id { get; set; }
    public string ChangeKey { get; set; }
    public string Reason { get; set; }
    public string Name { get; set; }
    public DateTimeOffset? OrderDateTime { get; set; }
}

public sealed class SubstrateTaskFolder
{
    /// <summary>Identical to the Graph todoTaskList id.</summary>
    public string Id { get; set; }
    public string ChangeKey { get; set; }
    public string Reason { get; set; }

    public string Name { get; set; }
    public string ParentFolderGroupId { get; set; }
    public bool IsDefaultFolder { get; set; }
    public DateTimeOffset? OrderDateTime { get; set; }

    /// <summary>Microsoft To Do palette name, for example "dark_red".</summary>
    public string ThemeColor { get; set; }

    public string ThemeBackground { get; set; }
    public int? SortType { get; set; }
    public bool? SortAscending { get; set; }
    public bool? ShowCompletedTasks { get; set; }
}

public sealed class SubstrateFolderGroupMutation
{
    public string Name { get; set; }
    public DateTimeOffset? OrderDateTime { get; set; }
}

public sealed class SubstrateTaskFolderMutation
{
    public string ParentFolderGroupId { get; set; }
    public DateTimeOffset? OrderDateTime { get; set; }
}
