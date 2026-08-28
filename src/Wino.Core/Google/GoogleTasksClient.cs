using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Google;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GoogleTaskListPage))]
[JsonSerializable(typeof(GoogleTaskPage))]
[JsonSerializable(typeof(GoogleTaskList))]
[JsonSerializable(typeof(GoogleTask))]
[JsonSerializable(typeof(GoogleTaskLink))]
internal partial class GoogleTasksJsonContext : JsonSerializerContext;

/// <summary>
/// Focused REST client for Google Tasks. The Google SDK does not expose a list
/// delta token, so callers receive complete paginated list/task representations.
/// </summary>
public sealed class GoogleTasksClient
{
    private const string Endpoint = "https://tasks.googleapis.com/tasks/v1";
    private readonly HttpClient _httpClient;

    public GoogleTasksClient(HttpClient httpClient) => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<IReadOnlyList<GoogleTaskList>> GetTaskListsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<GoogleTaskList>();
        string pageToken = null;
        do
        {
            var uri = $"{Endpoint}/users/@me/lists?maxResults=100" + (string.IsNullOrWhiteSpace(pageToken) ? string.Empty : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var page = await GetAsync(uri, GoogleTasksJsonContext.Default.GoogleTaskListPage, cancellationToken).ConfigureAwait(false);
            result.AddRange(page?.Items ?? []);
            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));
        return result;
    }

    public async Task<IReadOnlyList<GoogleTask>> GetTasksAsync(string taskListId, DateTimeOffset? updatedMin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskListId);
        var result = new List<GoogleTask>();
        string pageToken = null;
        do
        {
            var parameters = new List<string>
            {
                "maxResults=100",
                "showCompleted=true",
                "showDeleted=true",
                "showHidden=true",
                "showAssigned=false"
            };
            if (updatedMin is DateTimeOffset watermark)
                parameters.Add($"updatedMin={Uri.EscapeDataString(watermark.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))}");
            if (!string.IsNullOrWhiteSpace(pageToken))
                parameters.Add($"pageToken={Uri.EscapeDataString(pageToken)}");
            var uri = $"{Endpoint}/lists/{Uri.EscapeDataString(taskListId)}/tasks?{string.Join("&", parameters)}";
            var page = await GetAsync(uri, GoogleTasksJsonContext.Default.GoogleTaskPage, cancellationToken).ConfigureAwait(false);
            result.AddRange(page?.Items ?? []);
            pageToken = page?.NextPageToken;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));
        return result;
    }

    public Task<GoogleTaskList> CreateTaskListAsync(string title, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, $"{Endpoint}/users/@me/lists", new GoogleTaskList { Title = title }, null, GoogleTasksJsonContext.Default.GoogleTaskList, cancellationToken);

    public Task<GoogleTaskList> UpdateTaskListAsync(string listId, string title, string etag, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, $"{Endpoint}/users/@me/lists/{Uri.EscapeDataString(listId)}", new GoogleTaskList { Id = listId, Title = title }, etag, GoogleTasksJsonContext.Default.GoogleTaskList, cancellationToken);

    public Task<GoogleTaskList> GetTaskListAsync(string listId, CancellationToken cancellationToken = default)
        => GetAsync($"{Endpoint}/users/@me/lists/{Uri.EscapeDataString(listId)}", GoogleTasksJsonContext.Default.GoogleTaskList, cancellationToken);

    public Task DeleteTaskListAsync(string listId, string etag, CancellationToken cancellationToken = default)
        => SendAsync<object>(HttpMethod.Delete, $"{Endpoint}/users/@me/lists/{Uri.EscapeDataString(listId)}", null, etag, null, cancellationToken);

    public Task<GoogleTask> CreateTaskAsync(string listId, GoogleTask task, string parentTaskId = null, CancellationToken cancellationToken = default)
    {
        var url = $"{Endpoint}/lists/{Uri.EscapeDataString(listId)}/tasks";
        if (!string.IsNullOrWhiteSpace(parentTaskId))
            url += $"?parent={Uri.EscapeDataString(parentTaskId)}";
        return SendAsync(HttpMethod.Post, url, task, null, GoogleTasksJsonContext.Default.GoogleTask, cancellationToken);
    }

    public Task<GoogleTask> UpdateTaskAsync(string listId, string taskId, GoogleTask task, string etag, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, $"{Endpoint}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}", task, etag, GoogleTasksJsonContext.Default.GoogleTask, cancellationToken);

    public Task<GoogleTask> GetTaskAsync(string listId, string taskId, CancellationToken cancellationToken = default)
        => GetAsync($"{Endpoint}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}", GoogleTasksJsonContext.Default.GoogleTask, cancellationToken);

    public Task DeleteTaskAsync(string listId, string taskId, string etag, CancellationToken cancellationToken = default)
        => SendAsync<object>(HttpMethod.Delete, $"{Endpoint}/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}", null, etag, null, cancellationToken);

    private async Task<T> GetAsync<T>(string uri, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await GoogleApiErrorParser.ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object body, string etag, JsonTypeInfo<T> responseTypeInfo, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(etag))
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (body is not null)
        {
            request.Content = new StringContent(SerializeBody(body), Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await GoogleApiErrorParser.ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);
        if (typeof(T) == typeof(object) || response.Content.Headers.ContentLength == 0)
            return default;
        return await response.Content.ReadFromJsonAsync(responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }

    private static string SerializeBody(object body)
        => body switch
        {
            GoogleTaskList list => JsonSerializer.Serialize(list, GoogleTasksJsonContext.Default.GoogleTaskList),
            GoogleTask task => JsonSerializer.Serialize(task, GoogleTasksJsonContext.Default.GoogleTask),
            _ => throw new ArgumentException($"Unsupported Google Tasks request body: {body.GetType().Name}", nameof(body))
        };

}

public sealed class GoogleTaskListPage
{
    [JsonPropertyName("items")] public List<GoogleTaskList> Items { get; set; } = [];
    [JsonPropertyName("nextPageToken")] public string NextPageToken { get; set; }
}

public sealed class GoogleTaskPage
{
    [JsonPropertyName("items")] public List<GoogleTask> Items { get; set; } = [];
    [JsonPropertyName("nextPageToken")] public string NextPageToken { get; set; }
}

public sealed class GoogleTaskList
{
    [JsonPropertyName("kind")] public string Kind { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("etag")] public string Etag { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; }
    [JsonPropertyName("updated")] public DateTimeOffset? Updated { get; set; }
}

public sealed class GoogleTask
{
    [JsonPropertyName("kind")] public string Kind { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("etag")] public string Etag { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; }
    [JsonPropertyName("due")] public DateTimeOffset? Due { get; set; }
    [JsonPropertyName("completed")] public DateTimeOffset? Completed { get; set; }
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    [JsonPropertyName("updated")] public DateTimeOffset? Updated { get; set; }
    [JsonPropertyName("position")] public string Position { get; set; }
    [JsonPropertyName("parent")] public string Parent { get; set; }
    [JsonPropertyName("links")] public List<GoogleTaskLink> Links { get; set; } = [];
}

public sealed class GoogleTaskLink
{
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; }
    [JsonPropertyName("link")] public string Link { get; set; }
}
