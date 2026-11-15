#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Authentication.OAuth;

namespace Wino.Authentication.Exchange;

internal sealed record ExchangeAuthorizationCodeTokens(string AccessToken, string? RefreshToken, string? IdToken, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// The authorization code exchange of the Exchange embedded browser sign-in. It is kept apart from
/// <see cref="OAuthProtocolClient.ExchangeCodeAsync"/> because Exchange also needs the ID token, which the shared
/// client does not return, and the shared client's behaviour must not change for IMAP OAuth.
/// </summary>
internal static class ExchangeAuthorizationCodeClient
{
    private const int DefaultLifetimeSeconds = 3600;
    private const int MinimumLifetimeSeconds = 60;
    private const int MaximumLifetimeSeconds = 366 * 24 * 3600;

    // Redirects are not followed: the code and verifier must never be steered to another URL.
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinoMail", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public static async Task<ExchangeAuthorizationCodeTokens> ExchangeAsync(
        Uri tokenEndpoint,
        string clientId,
        string code,
        string codeVerifier,
        Uri redirectUri,
        string? resource,
        CancellationToken cancellationToken)
    {
        if (tokenEndpoint.Scheme != Uri.UriSchemeHttps)
            throw new OAuthProtocolException("The OAuth token_endpoint must be an absolute HTTPS URL.", "invalid_endpoint", null, isTransient: false);

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("client_id", clientId),
            new("code_verifier", codeVerifier)
        };

        if (!string.IsNullOrWhiteSpace(resource))
            form.Add(new("resource", resource));

        using var content = new FormUrlEncodedContent(form);

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new OAuthProtocolException("The token endpoint could not be reached.", null, null, isTransient: true, ex);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new OAuthProtocolException("The token response could not be read.", null, (int)response.StatusCode, isTransient: true, ex);
            }

            var statusCode = (int)response.StatusCode;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                // A body that is not JSON is usually a proxy page or an outage, not a verdict on the code.
                throw new OAuthProtocolException("The token response is not valid JSON.", null, statusCode, isTransient: true, ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new OAuthProtocolException("The token response is not a JSON object.", null, statusCode, isTransient: true);

                if (!response.IsSuccessStatusCode)
                {
                    var error = GetString(root, "error");
                    throw new OAuthProtocolException(
                        "The token request failed.",
                        error,
                        statusCode,
                        isTransient: error is null && statusCode >= 500);
                }

                var accessToken = GetString(root, "access_token");
                if (string.IsNullOrWhiteSpace(accessToken))
                    throw new OAuthProtocolException("The token response has no access_token.", null, statusCode, isTransient: false);

                var tokenType = GetString(root, "token_type");
                if (!string.IsNullOrWhiteSpace(tokenType) && !string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                    throw new OAuthProtocolException("The token endpoint returned an unsupported token type.", "unsupported_token_type", statusCode, isTransient: false);

                return new ExchangeAuthorizationCodeTokens(
                    accessToken,
                    NullIfWhiteSpace(GetString(root, "refresh_token")),
                    NullIfWhiteSpace(GetString(root, "id_token")),
                    DateTimeOffset.UtcNow.AddSeconds(ParseLifetimeSeconds(root)));
            }
        }
    }

    /// <summary>The payload claims of a compact JWT, or null when it is not one.</summary>
    public static JsonDocument? TryReadJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2 || string.IsNullOrEmpty(parts[1]))
            return null;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

        try
        {
            var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return document;

            document.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int ParseLifetimeSeconds(JsonElement root)
    {
        long seconds = 0;
        if (root.TryGetProperty("expires_in", out var expiresIn))
        {
            if (expiresIn.ValueKind == JsonValueKind.Number && expiresIn.TryGetInt64(out var number))
                seconds = number;
            else if (expiresIn.ValueKind == JsonValueKind.String
                     && long.TryParse(expiresIn.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                seconds = parsed;
        }

        if (seconds <= 0)
            seconds = DefaultLifetimeSeconds;

        return (int)Math.Clamp(seconds, MinimumLifetimeSeconds, MaximumLifetimeSeconds);
    }
}
