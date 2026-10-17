#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.PushRelay;

/// <summary>Body of <c>POST /subscriptions</c>.</summary>
public sealed record GraphSubscriptionCreateRequest(
    string ChangeType,
    string NotificationUrl,
    string? LifecycleNotificationUrl,
    string Resource,
    DateTimeOffset ExpirationDateTime,
    string ClientState);

/// <summary>Body of <c>PATCH /subscriptions/{id}</c>.</summary>
public sealed record GraphSubscriptionRenewRequest(DateTimeOffset ExpirationDateTime);

/// <summary>The subset of a Graph subscription the relay registration needs.</summary>
public sealed record GraphSubscriptionResponse(string? Id, DateTimeOffset? ExpirationDateTime, string? Resource, string? ChangeType);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GraphSubscriptionCreateRequest))]
[JsonSerializable(typeof(GraphSubscriptionRenewRequest))]
[JsonSerializable(typeof(GraphSubscriptionResponse))]
internal sealed partial class GraphSubscriptionJsonContext : JsonSerializerContext;

/// <summary>Thrown when Graph answers a subscription call with an error; <see cref="StatusCode"/> drives the caller's recovery.</summary>
public sealed class GraphSubscriptionException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>
/// The three Graph subscription calls the relay registration needs, over a plain <see cref="HttpClient"/> with the
/// account's token. The generated Kiota client covers <c>/me</c> only, so this stays hand-written and small.
/// </summary>
public sealed class GraphSubscriptionClient(HttpClient? httpClient = null)
{
    private const string BaseUri = "https://graph.microsoft.com/v1.0/subscriptions";
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Created with immutable ids, so change notifications name messages the way the synchronizer stores them.</summary>
    public Task<GraphSubscriptionResponse> CreateAsync(MailAccount account, IAuthenticator authenticator, GraphSubscriptionCreateRequest request, CancellationToken cancellationToken)
        => SendAsync(account, authenticator, HttpMethod.Post, BaseUri,
            () => JsonContent.Create(request, GraphSubscriptionJsonContext.Default.GraphSubscriptionCreateRequest), cancellationToken, immutableIds: true);

    public Task<GraphSubscriptionResponse> RenewAsync(MailAccount account, IAuthenticator authenticator, string subscriptionId, DateTimeOffset expiration, CancellationToken cancellationToken)
        => SendAsync(account, authenticator, HttpMethod.Patch, $"{BaseUri}/{Uri.EscapeDataString(subscriptionId)}",
            () => JsonContent.Create(new GraphSubscriptionRenewRequest(expiration), GraphSubscriptionJsonContext.Default.GraphSubscriptionRenewRequest), cancellationToken);

    public async Task DeleteAsync(MailAccount account, IAuthenticator authenticator, string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(account, authenticator, HttpMethod.Delete, $"{BaseUri}/{Uri.EscapeDataString(subscriptionId)}", null, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphSubscriptionException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone.
        }
    }

    private async Task<GraphSubscriptionResponse> SendAsync(MailAccount account, IAuthenticator authenticator, HttpMethod method, string uri,
        Func<HttpContent>? content, CancellationToken cancellationToken, bool immutableIds = false)
    {
        account.ThrowIfNetworkAccessBlocked();
        var token = await authenticator.GetTokenInformationAsync(account, cancellationToken: cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        if (immutableIds)
            request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = content?.Invoke();

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return new GraphSubscriptionResponse(null, null, null, null);

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new GraphSubscriptionException(response.StatusCode, $"Graph {method.Method} subscriptions failed with HTTP {(int)response.StatusCode}: {Truncate(text)}");
        }

        try
        {
            return await response.Content.ReadFromJsonAsync(GraphSubscriptionJsonContext.Default.GraphSubscriptionResponse, cancellationToken).ConfigureAwait(false)
                ?? throw new GraphSubscriptionException(response.StatusCode, "Graph returned an empty subscription.");
        }
        catch (JsonException ex)
        {
            throw new GraphSubscriptionException(response.StatusCode, $"Graph returned an unreadable subscription: {ex.Message}");
        }
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
}
