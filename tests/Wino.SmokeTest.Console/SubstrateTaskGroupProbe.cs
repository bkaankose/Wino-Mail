using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Wino.SmokeTest.ConsoleApp;

/// <summary>
/// Phase 0 spike for To Do list groups.
///
/// Graph's To Do API exposes no group resource, and the Outlook Tasks API that used to own
/// outlookTaskGroup stopped returning data in August 2022. The Microsoft To Do clients instead
/// call the undocumented substrate API, which does have a foldergroups endpoint. This probe
/// answers the two questions that decide whether Wino can do the same:
///
///   1. Can Wino's own app registration obtain a token for the Exchange Online resource?
///   2. Do the folder ids substrate returns line up with the Graph task list ids Wino stores?
///
/// It only reads, and it stays outside the product code until those answers are in.
/// </summary>
internal static class SubstrateTaskGroupProbe
{
    private const string OutlookClientId = "b19c2035-d740-49ff-b297-de6ec561b208";
    private const string Authority = "https://login.microsoftonline.com/common";
    private const string TokenCacheFileName = "OutlookCache.bin";

    private static readonly string[] SubstrateScopes = ["https://outlook.office.com/Tasks.ReadWrite"];
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/Tasks.ReadWrite"];

    // The clients talk to substrate.office.com; the same API answers on the Outlook host, which is
    // the one an ordinary app registration is scoped to. Try that first, then the original.
    private static readonly string[] SubstrateHosts =
    [
        "https://outlook.office.com",
        "https://substrate.office.com"
    ];

    public static async Task<int> RunAsync(string address, string applicationDataFolder, CancellationToken cancellationToken)
    {
        ConsoleOutput.Header($"\nSubstrate task group probe: {address}");

        var application = PublicClientApplicationBuilder
            .Create(OutlookClientId)
            .WithAuthority(Authority)
            .WithDefaultRedirectUri()
            .Build();

        var cache = await MsalCacheHelper
            .CreateAsync(new StorageCreationPropertiesBuilder(TokenCacheFileName, applicationDataFolder).Build())
            .ConfigureAwait(false);
        cache.RegisterCache(application.UserTokenCache);

        var substrateToken = await AcquireAsync(application, SubstrateScopes, address, cancellationToken).ConfigureAwait(false);
        if (substrateToken is null)
        {
            ConsoleOutput.Error(
                "  No token for https://outlook.office.com/Tasks.ReadWrite.\n" +
                "  Add the Office 365 Exchange Online delegated permission Tasks.ReadWrite\n" +
                "  (resource 00000002-0000-0ff1-ce00-000000000000, permission\n" +
                "  6b49b74d-642f-4417-a6b4-820576845707) to the app registration manifest,\n" +
                "  then sign in once so the new resource is consented.");
            return 1;
        }

        ConsoleOutput.Success("  Substrate token acquired.");

        using var client = new HttpClient();
        var folderGroups = await GetFirstAsync(client, substrateToken, "/todob2/api/v1/foldergroups", cancellationToken).ConfigureAwait(false);
        var taskFolders = await GetFirstAsync(client, substrateToken, "/todob2/api/v1/taskfolders", cancellationToken).ConfigureAwait(false);

        Dump("foldergroups", folderGroups);
        Dump("taskfolders", taskFolders);

        var graphToken = await AcquireAsync(application, GraphScopes, address, cancellationToken).ConfigureAwait(false);
        if (graphToken is not null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/todo/lists");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Dump("graph /me/todo/lists", ((int)response.StatusCode, body));
        }
        else
        {
            ConsoleOutput.Error("  No Graph token, so the id shapes cannot be compared side by side.");
        }

        SummarizeSortTypes(taskFolders);
        await DumpGraphTasksAsync(client, graphToken, substrateToken, cancellationToken).ConfigureAwait(false);

        return folderGroups is { Status: 200 } ? 0 : 1;
    }

    /// <summary>
    /// Dumps the tasks of every list from Graph and from substrate, so a task that appears twice
    /// in Wino can be traced to either two real server items or one item seen under two ids.
    /// </summary>
    private static async Task DumpGraphTasksAsync(
        HttpClient client, string? graphToken, string substrateToken, CancellationToken cancellationToken)
    {
        if (graphToken is null)
            return;

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/todo/lists");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
        using var listResponse = await client.SendAsync(listRequest, cancellationToken).ConfigureAwait(false);
        using var lists = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        foreach (var list in lists.RootElement.GetProperty("value").EnumerateArray())
        {
            var id = list.GetProperty("id").GetString();
            var name = list.GetProperty("displayName").GetString();
            ConsoleOutput.Header($"\n  --- tasks in {name} ---");

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/me/todo/lists/{Uri.EscapeDataString(id!)}/tasks?$top=100");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ConsoleOutput.Error($"  {(int)response.StatusCode}");
                continue;
            }

            using var document = JsonDocument.Parse(body);
            foreach (var task in document.RootElement.GetProperty("value").EnumerateArray())
            {
                var title = task.TryGetProperty("title", out var t) ? t.GetString() : "?";
                var taskId = task.TryGetProperty("id", out var i) ? i.GetString() : "?";
                System.Console.WriteLine($"  {title,-28} {taskId}");
            }
        }
    }

    /// <summary>
    /// SortType is an undocumented integer. Wino only maps values that have been confirmed against
    /// a real mailbox, so this prints what each list currently reports: set a different sort on a
    /// few lists in Microsoft To Do, re-run, and the mapping can be filled in from the diff.
    /// </summary>
    private static void SummarizeSortTypes((int Status, string Body)? taskFolders)
    {
        if (taskFolders is not { Status: 200 })
            return;

        ConsoleOutput.Header("\n  --- per-list sort ---");

        try
        {
            using var document = JsonDocument.Parse(taskFolders.Value.Body);
            foreach (var folder in document.RootElement.GetProperty("Value").EnumerateArray())
            {
                var name = folder.TryGetProperty("Name", out var n) ? n.GetString() : "?";
                var sortType = folder.TryGetProperty("SortType", out var t) ? t.ToString() : "?";
                var ascending = folder.TryGetProperty("SortAscending", out var a) ? a.ToString() : "?";
                var showCompleted = folder.TryGetProperty("ShowCompletedTasks", out var c) ? c.ToString() : "?";
                var theme = folder.TryGetProperty("ThemeColor", out var th) ? th.GetString() : "?";

                System.Console.WriteLine(
                    $"  {name,-24} SortType={sortType,-4} Ascending={ascending,-6} ShowCompleted={showCompleted,-6} ThemeColor={theme}");
            }
        }
        catch (JsonException)
        {
            ConsoleOutput.Error("  could not read taskfolders payload");
        }
    }

    private static async Task<string?> AcquireAsync(
        IPublicClientApplication application,
        string[] scopes,
        string address,
        CancellationToken cancellationToken)
    {
        var accounts = (await application.GetAccountsAsync().ConfigureAwait(false)).ToList();
        var account = accounts.FirstOrDefault(candidate =>
            string.Equals(candidate.Username?.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase));

        if (account is not null)
        {
            try
            {
                var result = await application.AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // A new resource always needs consent once, and the app's tokens may live in the
                // Windows broker rather than this file cache. Fall through to device code.
            }
        }
        else
        {
            System.Console.WriteLine($"  {address} is not in {TokenCacheFileName}; falling back to device code.");
        }

        try
        {
            var result = await application
                .AcquireTokenWithDeviceCode(scopes, callback =>
                {
                    System.Console.WriteLine();
                    System.Console.WriteLine($"  {callback.Message}");
                    return Task.CompletedTask;
                })
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);

            return result.AccessToken;
        }
        catch (MsalException exception)
        {
            ConsoleOutput.Error($"  Token request failed: {exception.ErrorCode} {exception.Message}");
            return null;
        }
    }

    private static async Task<(int Status, string Body)?> GetFirstAsync(
        HttpClient client,
        string token,
        string path,
        CancellationToken cancellationToken)
    {
        (int Status, string Body)? lastFailure = null;

        foreach (var host in SubstrateHosts)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, host + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            try
            {
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                System.Console.WriteLine($"  GET {host}{path} -> {(int)response.StatusCode}");

                if (response.IsSuccessStatusCode)
                    return ((int)response.StatusCode, body);

                lastFailure = ((int)response.StatusCode, body);
            }
            catch (HttpRequestException exception)
            {
                ConsoleOutput.Error($"  GET {host}{path} failed: {exception.Message}");
            }
        }

        return lastFailure;
    }

    private static void Dump(string label, (int Status, string Body)? response)
    {
        ConsoleOutput.Header($"\n  --- {label} ---");

        if (response is null)
        {
            ConsoleOutput.Error("  no response");
            return;
        }

        System.Console.WriteLine($"  status: {response.Value.Status}");
        System.Console.WriteLine(Prettify(response.Value.Body));
    }

    private static string Prettify(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "  (empty)";

        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return body;
        }
    }
}
