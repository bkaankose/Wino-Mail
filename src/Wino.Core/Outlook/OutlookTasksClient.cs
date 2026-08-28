using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Wino.Core.Outlook;

/// <summary>
/// Small Graph v1 adapter for To Do. Keeping the URLs here makes delta-link
/// continuation and page-complete cursor commits explicit and testable.
/// </summary>
public sealed class OutlookTasksClient
{
    private static readonly Dictionary<string, ParsableFactory<IParsable>> ErrorMapping = new()
    {
        ["4XX"] = ODataError.CreateFromDiscriminatorValue,
        ["5XX"] = ODataError.CreateFromDiscriminatorValue
    };

    private readonly IRequestAdapter _adapter;

    public OutlookTasksClient(IRequestAdapter adapter) => _adapter = adapter;

    public Task<OutlookTaskListCollectionResponse> GetTaskListsAsync(string url = null, CancellationToken cancellationToken = default)
        => SendCollectionAsync(url ?? "https://graph.microsoft.com/v1.0/me/todo/lists?$top=100", OutlookTaskListCollectionResponse.CreateFromDiscriminatorValue, cancellationToken);

    public Task<OutlookTaskListCollectionResponse> GetTaskListsDeltaAsync(string url = null, CancellationToken cancellationToken = default)
        => SendCollectionAsync(url ?? "https://graph.microsoft.com/v1.0/me/todo/lists/delta", OutlookTaskListCollectionResponse.CreateFromDiscriminatorValue, cancellationToken);

    public Task<OutlookTaskCollectionResponse> GetTasksDeltaAsync(string listId, string url = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listId);
        // Graph rejects $top on the To Do task delta endpoint and reports it as the
        // misleading "Delta query is not supported by this resource." Page size is
        // negotiated through the Prefer header instead.
        url ??= $"https://graph.microsoft.com/v1.0/me/todo/lists/{Uri.EscapeDataString(listId)}/tasks/delta()?$expand=checklistItems,extensions";
        url = NormalizeTaskDeltaUrl(url);
        return SendCollectionAsync(url, OutlookTaskCollectionResponse.CreateFromDiscriminatorValue, cancellationToken);
    }

    public Task<TodoTask> GetTaskAsync(string listId, string taskId, CancellationToken cancellationToken = default)
        => SendAsync<TodoTask>($"{BuildTaskUrl(listId, taskId)}?$expand=checklistItems,extensions", Method.GET, TodoTask.CreateFromDiscriminatorValue, cancellationToken);

    public Task<TodoTaskList> GetTaskListAsync(string listId, CancellationToken cancellationToken = default)
        => SendAsync<TodoTaskList>($"https://graph.microsoft.com/v1.0/me/todo/lists/{Uri.EscapeDataString(listId)}", Method.GET, TodoTaskList.CreateFromDiscriminatorValue, cancellationToken);

    public Task<TodoTaskList> CreateTaskListAsync(TodoTaskList list, CancellationToken cancellationToken = default)
        => SendParsableAsync("https://graph.microsoft.com/v1.0/me/todo/lists", Method.POST, list, TodoTaskList.CreateFromDiscriminatorValue, null, cancellationToken);

    public Task<TodoTaskList> UpdateTaskListAsync(string listId, TodoTaskList list, CancellationToken cancellationToken = default)
        => UpdateTaskListAsync(listId, list, null, cancellationToken);

    public Task<TodoTaskList> UpdateTaskListAsync(string listId, TodoTaskList list, string ifMatch, CancellationToken cancellationToken = default)
        => SendParsableAsync($"https://graph.microsoft.com/v1.0/me/todo/lists/{Uri.EscapeDataString(listId)}", Method.PATCH, list, TodoTaskList.CreateFromDiscriminatorValue, ifMatch, cancellationToken);

    public Task DeleteTaskListAsync(string listId, CancellationToken cancellationToken = default)
        => DeleteTaskListAsync(listId, null, cancellationToken);

    public Task DeleteTaskListAsync(string listId, string ifMatch, CancellationToken cancellationToken = default)
        => SendNoContentAsync($"https://graph.microsoft.com/v1.0/me/todo/lists/{Uri.EscapeDataString(listId)}", Method.DELETE, ifMatch, cancellationToken);

    public Task<TodoTask> CreateTaskAsync(string listId, TodoTask task, CancellationToken cancellationToken = default)
        => SendParsableAsync($"https://graph.microsoft.com/v1.0/me/todo/lists/{Uri.EscapeDataString(listId)}/tasks", Method.POST, task, TodoTask.CreateFromDiscriminatorValue, null, cancellationToken);

    public Task<TodoTask> UpdateTaskAsync(string listId, string taskId, TodoTask task, CancellationToken cancellationToken = default)
        => UpdateTaskAsync(listId, taskId, task, null, cancellationToken);

    public Task<TodoTask> UpdateTaskAsync(string listId, string taskId, TodoTask task, string ifMatch, CancellationToken cancellationToken = default)
        => SendParsableAsync(BuildTaskUrl(listId, taskId), Method.PATCH, task, TodoTask.CreateFromDiscriminatorValue, ifMatch, cancellationToken);

    public Task DeleteTaskAsync(string listId, string taskId, CancellationToken cancellationToken = default)
        => DeleteTaskAsync(listId, taskId, null, cancellationToken);

    public Task DeleteTaskAsync(string listId, string taskId, string ifMatch, CancellationToken cancellationToken = default)
        => SendNoContentAsync(BuildTaskUrl(listId, taskId), Method.DELETE, ifMatch, cancellationToken);

    // Graph refuses checklistItems inside a task PATCH ("Update on checklistItems
    // navigation property is not supported in PATCH request on task entity"), so step
    // changes on an existing task go through this child collection. Checklist items
    // carry no ETag, so these calls send no If-Match.
    public Task<ChecklistItem> CreateChecklistItemAsync(string listId, string taskId, ChecklistItem item, CancellationToken cancellationToken = default)
        => SendParsableAsync($"{BuildTaskUrl(listId, taskId)}/checklistItems", Method.POST, item, ChecklistItem.CreateFromDiscriminatorValue, null, cancellationToken);

    public Task<ChecklistItem> UpdateChecklistItemAsync(string listId, string taskId, string checklistItemId, ChecklistItem item, CancellationToken cancellationToken = default)
        => SendParsableAsync(BuildChecklistItemUrl(listId, taskId, checklistItemId), Method.PATCH, item, ChecklistItem.CreateFromDiscriminatorValue, null, cancellationToken);

    public Task DeleteChecklistItemAsync(string listId, string taskId, string checklistItemId, CancellationToken cancellationToken = default)
        => SendNoContentAsync(BuildChecklistItemUrl(listId, taskId, checklistItemId), Method.DELETE, null, cancellationToken);

    private async Task<T> SendCollectionAsync<T>(string url, ParsableFactory<T> factory, CancellationToken cancellationToken) where T : IParsable
    {
        var request = CreateRequest(url, Method.GET);
        request.Headers.Add("Prefer", "odata.maxpagesize=100");
        return await _adapter.SendAsync(request, factory, ErrorMapping, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(string url, Method method, ParsableFactory<T> factory, CancellationToken cancellationToken) where T : IParsable
    {
        var request = CreateRequest(url, method);
        return await _adapter.SendAsync(request, factory, ErrorMapping, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> SendParsableAsync<T>(string url, Method method, T value, ParsableFactory<T> factory, string ifMatch, CancellationToken cancellationToken) where T : IParsable
    {
        var request = CreateRequest(url, method, ifMatch);
        request.SetContentFromParsable(_adapter, "application/json", value);
        return await _adapter.SendAsync(request, factory, ErrorMapping, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendNoContentAsync(string url, Method method, string ifMatch, CancellationToken cancellationToken)
    {
        var request = CreateRequest(url, method, ifMatch);
        await _adapter.SendNoContentAsync(request, ErrorMapping, cancellationToken).ConfigureAwait(false);
    }

    private static RequestInformation CreateRequest(string url, Method method, string ifMatch = null)
    {
        var request = new RequestInformation { URI = new Uri(url), HttpMethod = method };
        if (!string.IsNullOrWhiteSpace(ifMatch))
            request.Headers.TryAdd("If-Match", ifMatch);
        return request;
    }

    private static string BuildTaskUrl(string listId, string taskId)
        => $"https://graph.microsoft.com/v1.0/me/todo/lists/{Uri.EscapeDataString(listId)}/tasks/{Uri.EscapeDataString(taskId)}";

    private static string BuildChecklistItemUrl(string listId, string taskId, string checklistItemId)
        => $"{BuildTaskUrl(listId, taskId)}/checklistItems/{Uri.EscapeDataString(checklistItemId)}";

    private static string NormalizeTaskDeltaUrl(string url)
    {
        var uri = new Uri(url);
        if (!uri.AbsolutePath.EndsWith("/tasks/delta", StringComparison.OrdinalIgnoreCase))
            return url;

        var builder = new UriBuilder(uri) { Path = $"{uri.AbsolutePath}()" };
        return builder.Uri.AbsoluteUri;
    }
}

public sealed class OutlookTaskListCollectionResponse : IParsable
{
    public List<TodoTaskList> Value { get; set; } = [];
    public string NextLink { get; set; }
    public string DeltaLink { get; set; }
    public static OutlookTaskListCollectionResponse CreateFromDiscriminatorValue(IParseNode _) => new();
    public IDictionary<string, Action<IParseNode>> GetFieldDeserializers() => new Dictionary<string, Action<IParseNode>>
    {
        ["value"] = node => Value = node.GetCollectionOfObjectValues<TodoTaskList>(TodoTaskList.CreateFromDiscriminatorValue)?.ToList() ?? [],
        ["@odata.nextLink"] = node => NextLink = node.GetStringValue(),
        ["@odata.deltaLink"] = node => DeltaLink = node.GetStringValue()
    };
    public void Serialize(ISerializationWriter writer) { }
}

public sealed class OutlookTaskCollectionResponse : IParsable
{
    public List<TodoTask> Value { get; set; } = [];
    public string NextLink { get; set; }
    public string DeltaLink { get; set; }
    public static OutlookTaskCollectionResponse CreateFromDiscriminatorValue(IParseNode _) => new();
    public IDictionary<string, Action<IParseNode>> GetFieldDeserializers() => new Dictionary<string, Action<IParseNode>>
    {
        ["value"] = node => Value = node.GetCollectionOfObjectValues<TodoTask>(TodoTask.CreateFromDiscriminatorValue)?.ToList() ?? [],
        ["@odata.nextLink"] = node => NextLink = node.GetStringValue(),
        ["@odata.deltaLink"] = node => DeltaLink = node.GetStringValue()
    };
    public void Serialize(ISerializationWriter writer) { }
}
