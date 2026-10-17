#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.PushRelay.Socket.Transport;

/// <summary>Reads WinoRelay problem documents (<c>application/problem+json</c>) into <see cref="PushRelayProblem"/>.</summary>
public static class RelaySocketProblemReader
{
    private const string ProblemTypePrefix = "urn:winorelay:error:";

    public static async Task<PushRelayProblem?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var retryAfter = ReadRetryAfter(response);
        string? payload = null;
        try
        {
            if (response.Content is not null && !IsHtml(response))
                payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException)
        {
            // A body that cannot be read is treated as no body.
        }

        return Parse(payload, status, retryAfter);
    }

    public static PushRelayProblem? Parse(string? payload, int status, int? retryAfterSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return Fallback(status, retryAfterSeconds);

        try
        {
            var fields = JsonSerializer.Deserialize(payload, RelaySocketJsonContext.Default.DictionaryStringJsonElement);
            if (fields is not null && ReadString(fields, "type") is { } type)
            {
                var code = type.StartsWith(ProblemTypePrefix, StringComparison.Ordinal) ? type[ProblemTypePrefix.Length..] : type;
                return new PushRelayProblem(code, status, ReadString(fields, "title"), ReadString(fields, "detail"),
                    ReadString(fields, "reason"), ReadInt(fields, "retryAfterSeconds") ?? retryAfterSeconds);
            }
        }
        catch (JsonException)
        {
            // Not a problem document; fall through.
        }

        return Fallback(status, retryAfterSeconds);
    }

    private static PushRelayProblem? Fallback(int status, int? retryAfterSeconds)
    {
        if (status is >= 200 and < 300) return null;
        var code = status switch
        {
            401 => PushRelayErrorCodes.Unauthorized,
            403 => PushRelayErrorCodes.Forbidden,
            429 => PushRelayErrorCodes.RateLimited,
            >= 500 or 408 => PushRelayErrorCodes.ServiceUnavailable,
            _ => PushRelayErrorCodes.InvalidResponse,
        };
        return new PushRelayProblem(code, status, RetryAfterSeconds: retryAfterSeconds);
    }

    private static int? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter) return null;
        if (retryAfter.Delta is { } delta) return (int)Math.Ceiling(delta.TotalSeconds);
        return retryAfter.Date is { } date ? Math.Max(0, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds)) : null;
    }

    private static string? ReadString(Dictionary<string, JsonElement> fields, string name)
        => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static bool IsHtml(HttpResponseMessage response)
        => response.Content?.Headers.ContentType?.MediaType is string mediaType
           && (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
}
