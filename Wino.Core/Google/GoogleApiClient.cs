using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Google;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.PeopleService.v1.Data;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Wino.Core.Google;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GoogleApiErrorEnvelope))]
[JsonSerializable(typeof(GoogleEmptyResponse))]
[JsonSerializable(typeof(BatchDeleteMessagesRequest))]
[JsonSerializable(typeof(BatchModifyMessagesRequest))]
[JsonSerializable(typeof(CalendarList))]
[JsonSerializable(typeof(Draft))]
[JsonSerializable(typeof(Event))]
[JsonSerializable(typeof(Events))]
[JsonSerializable(typeof(Filter))]
[JsonSerializable(typeof(Label))]
[JsonSerializable(typeof(ListDraftsResponse))]
[JsonSerializable(typeof(ListHistoryResponse))]
[JsonSerializable(typeof(ListFiltersResponse))]
[JsonSerializable(typeof(ListLabelsResponse))]
[JsonSerializable(typeof(ListMessagesResponse))]
[JsonSerializable(typeof(ListSendAsResponse))]
[JsonSerializable(typeof(Message))]
[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(Profile))]
[JsonSerializable(typeof(DriveFile), TypeInfoPropertyName = "DriveFile")]
internal partial class GoogleApiJsonContext : JsonSerializerContext;

public sealed class GoogleEmptyResponse
{
}

public interface IGoogleApiRequest
{
    object Service { get; }

    HttpRequestMessage CreateHttpRequestMessage();

    Task<HttpResponseMessage> ExecuteAsHttpResponseAsync(CancellationToken cancellationToken = default);
}

public interface IGoogleBatchService
{
    HttpClient HttpClient { get; }

    string BatchEndpoint { get; }
}

public interface IGoogleApiRequest<T> : IGoogleApiRequest
{
    Task<T> DeserializeResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken = default);
}

public abstract class GoogleApiRequest<T> : IGoogleApiRequest<T>
{
    private readonly HttpClient _httpClient;
    private readonly HttpMethod _method;
    private readonly Func<HttpContent> _contentFactory;
    private readonly JsonTypeInfo<T> _responseTypeInfo;

    protected GoogleApiRequest(
        HttpClient httpClient,
        object service,
        HttpMethod method,
        Func<string> requestUriFactory,
        JsonTypeInfo<T> responseTypeInfo,
        Func<HttpContent> contentFactory = null)
    {
        _httpClient = httpClient;
        Service = service;
        _method = method;
        RequestUriFactory = requestUriFactory;
        _responseTypeInfo = responseTypeInfo;
        _contentFactory = contentFactory;
    }

    public object Service { get; }

    protected Func<string> RequestUriFactory { get; set; }

    public HttpRequestMessage CreateHttpRequestMessage()
    {
        var request = new HttpRequestMessage(_method, RequestUriFactory());
        request.Content = _contentFactory?.Invoke();
        return request;
    }

    public async Task<HttpResponseMessage> ExecuteAsHttpResponseAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateHttpRequestMessage();
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var response = await ExecuteAsHttpResponseAsync(cancellationToken).ConfigureAwait(false);
        await GoogleApiErrorParser.ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);
        return await DeserializeResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<T> ExecuteAsync() => ExecuteAsync(CancellationToken.None);

    public T Execute() => ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<T> DeserializeResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.Content.Headers.ContentLength == 0)
            return default;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, _responseTypeInfo, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GoogleRequestError
{
    public int Code { get; init; }

    public List<GoogleApiErrorDetail> Errors { get; init; }

    public string Message { get; init; }
}

public sealed class GoogleBatchRequest
{
    private readonly IGoogleBatchService _service;
    private readonly List<QueuedRequest> _requests = [];

    public GoogleBatchRequest(object service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service as IGoogleBatchService;
    }

    public void Queue<T>(
        IGoogleApiRequest request,
        Action<T, GoogleRequestError, int, HttpResponseMessage> callback)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callback);

        _requests.Add(new QueuedRequest(
            request,
            async (response, index, cancellationToken) =>
            {
                if (!response.IsSuccessStatusCode)
                {
                    var error = await GoogleApiErrorParser.ParseAsync(response, cancellationToken).ConfigureAwait(false);
                    callback(default, error, index, response);
                    return;
                }

                T content = default;
                if (request is IGoogleApiRequest<T> typedRequest)
                {
                    content = await typedRequest.DeserializeResponseAsync(response, cancellationToken).ConfigureAwait(false);
                }

                callback(content, null, index, response);
            },
            (exception, index) =>
            {
                callback(default, CreateTransportError(exception), index, null);
                return Task.CompletedTask;
            }));
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (_requests.Count == 0)
            return;

        if (_service != null)
        {
            await ExecuteMultipartBatchAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        for (var index = 0; index < _requests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response = null;

            try
            {
                response = await _requests[index].Request.ExecuteAsHttpResponseAsync(cancellationToken).ConfigureAwait(false);
                await _requests[index].ProcessResponseAsync(response, index, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _requests[index].ProcessTransportErrorAsync(ex, index).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteMultipartBatchAsync(CancellationToken cancellationToken)
    {
        var boundary = $"wino_batch_{Guid.NewGuid():N}";
        using var multipart = new MultipartContent("mixed", boundary);

        for (var index = 0; index < _requests.Count; index++)
        {
            using var innerRequest = _requests[index].Request.CreateHttpRequestMessage();
            var wireRequest = await CreateWireRequestAsync(innerRequest, cancellationToken).ConfigureAwait(false);
            var requestContent = new StringContent(wireRequest, System.Text.Encoding.UTF8);
            requestContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/http");
            requestContent.Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("msgtype", "request"));
            requestContent.Headers.TryAddWithoutValidation("Content-ID", $"<item-{index}>");
            requestContent.Headers.Add("Content-Transfer-Encoding", "binary");
            multipart.Add(requestContent);
        }

        using var batchResponse = await _service.HttpClient.PostAsync(_service.BatchEndpoint, multipart, cancellationToken).ConfigureAwait(false);
        await GoogleApiErrorParser.ThrowIfUnsuccessfulAsync(batchResponse, cancellationToken).ConfigureAwait(false);

        var responsePayload = await batchResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var responseBoundary = GetBoundary(batchResponse.Content.Headers.ContentType) ?? boundary;
        var responses = ParseMultipartResponses(responsePayload, responseBoundary);

        if (responses.Count != _requests.Count)
        {
            throw new InvalidOperationException(
                $"Google batch response contained {responses.Count} parts for {_requests.Count} requests.");
        }

        for (var index = 0; index < _requests.Count; index++)
        {
            await _requests[index].ProcessResponseAsync(responses[index], index, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> CreateWireRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var relativeUri = uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
        var builder = new System.Text.StringBuilder();
        builder.Append(request.Method.Method).Append(' ').Append(relativeUri).Append(" HTTP/1.1\r\n");

        foreach (var header in request.Headers)
            builder.Append(header.Key).Append(": ").AppendJoin(", ", header.Value).Append("\r\n");

        string body = null;
        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
                builder.Append(header.Key).Append(": ").AppendJoin(", ", header.Value).Append("\r\n");

            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        builder.Append("\r\n");
        if (!string.IsNullOrEmpty(body))
            builder.Append(body);

        return builder.ToString();
    }

    private static string GetBoundary(System.Net.Http.Headers.MediaTypeHeaderValue contentType)
        => contentType?.Parameters?
            .FirstOrDefault(parameter => parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))?
            .Value?
            .Trim('"');

    private static List<HttpResponseMessage> ParseMultipartResponses(string payload, string boundary)
    {
        var responses = new List<HttpResponseMessage>();
        var delimiter = $"--{boundary}";
        var parts = payload.Split(delimiter, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawPart in parts)
        {
            var part = rawPart.Trim('\r', '\n', '-', ' ');
            if (string.IsNullOrWhiteSpace(part))
                continue;

            var responseStart = part.IndexOf("HTTP/", StringComparison.OrdinalIgnoreCase);
            if (responseStart < 0)
                continue;

            var responseText = part[responseStart..];
            var statusLineEnd = responseText.IndexOf("\r\n", StringComparison.Ordinal);
            if (statusLineEnd < 0)
                throw new InvalidOperationException("Google batch response contained an invalid HTTP status line.");

            var statusLine = responseText[..statusLineEnd].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (statusLine.Length < 2 || !int.TryParse(statusLine[1], out var statusCode))
                throw new InvalidOperationException("Google batch response contained an invalid HTTP status code.");

            var headersStart = statusLineEnd + 2;
            var headersEnd = responseText.IndexOf("\r\n\r\n", headersStart, StringComparison.Ordinal);
            var headerText = headersEnd >= 0 ? responseText[headersStart..headersEnd] : responseText[headersStart..];
            var body = headersEnd >= 0 ? responseText[(headersEnd + 4)..].TrimEnd('\r', '\n') : string.Empty;

            var response = new HttpResponseMessage((HttpStatusCode)statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };

            foreach (var headerLine in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = headerLine.IndexOf(':');
                if (separator <= 0)
                    continue;

                var name = headerLine[..separator].Trim();
                var value = headerLine[(separator + 1)..].Trim();
                if (!response.Headers.TryAddWithoutValidation(name, value))
                    response.Content.Headers.TryAddWithoutValidation(name, value);
            }

            responses.Add(response);
        }

        return responses;
    }

    private static GoogleRequestError CreateTransportError(Exception ex) => new()
    {
        Code = 0,
        Message = ex.Message,
        Errors = [new GoogleApiErrorDetail { Message = ex.Message, Reason = ex.GetType().Name }]
    };

    private sealed record QueuedRequest(
        IGoogleApiRequest Request,
        Func<HttpResponseMessage, int, CancellationToken, Task> ProcessResponseAsync,
        Func<Exception, int, Task> ProcessTransportErrorAsync)
    {
    }
}

public enum GoogleUploadStatus
{
    NotStarted,
    Uploading,
    Completed,
    Failed
}

public sealed record GoogleUploadProgress(GoogleUploadStatus Status, Exception Exception = null);

internal static class GoogleApiErrorParser
{
    public static async Task ThrowIfUnsuccessfulAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var parsed = await ParseEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        throw new GoogleApiException(response.StatusCode, parsed);
    }

    public static async Task<GoogleRequestError> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var parsed = await ParseEnvelopeAsync(response, cancellationToken).ConfigureAwait(false);
        return new GoogleRequestError
        {
            Code = parsed.Code == 0 ? (int)response.StatusCode : parsed.Code,
            Message = parsed.Message ?? response.ReasonPhrase,
            Errors = parsed.Errors ?? []
        };
    }

    private static async Task<GoogleApiError> ParseEnvelopeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var envelope = await JsonSerializer.DeserializeAsync(
                stream,
                GoogleApiJsonContext.Default.GoogleApiErrorEnvelope,
                cancellationToken).ConfigureAwait(false);

            if (envelope?.Error != null)
                return envelope.Error;
        }
        catch (JsonException)
        {
            // Google occasionally returns a proxy-generated non-JSON response. Preserve the HTTP status below.
        }

        return new GoogleApiError
        {
            Code = (int)response.StatusCode,
            Message = response.ReasonPhrase ?? $"Google API request failed with HTTP {(int)response.StatusCode}.",
            Errors = []
        };
    }
}

internal static class GoogleUrl
{
    public static string Segment(string value) => Uri.EscapeDataString(value ?? string.Empty);

    public static string AddQuery(string baseUri, params (string Name, string Value)[] parameters)
    {
        var values = parameters.Where(parameter => !string.IsNullOrEmpty(parameter.Value)).ToList();
        if (values.Count == 0)
            return baseUri;

        return $"{baseUri}?{string.Join("&", values.Select(parameter => $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}"))}";
    }

    public static string AddRepeatedQuery(
        string baseUri,
        IEnumerable<(string Name, string Value)> parameters)
    {
        var values = parameters.Where(parameter => !string.IsNullOrEmpty(parameter.Value)).ToList();
        if (values.Count == 0)
            return baseUri;

        return $"{baseUri}?{string.Join("&", values.Select(parameter => $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}"))}";
    }

    public static string Boolean(bool? value) => value.HasValue ? value.Value.ToString().ToLowerInvariant() : null;

    public static string Number<T>(T? value) where T : struct, IFormattable
        => value?.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
}

internal static class GoogleJsonContent
{
    public static HttpContent Create<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonContent.Create(value, typeInfo);
}
