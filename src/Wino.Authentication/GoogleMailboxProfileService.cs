#nullable enable
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication;

public sealed class GoogleMailboxProfileService : IGoogleMailboxProfileService
{
    private const string UserInfoUrl = "https://openidconnect.googleapis.com/v1/userinfo";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly ILogger Logger = Log.ForContext<GoogleMailboxProfileService>();

    private readonly HttpClient _httpClient;

    public GoogleMailboxProfileService() : this(new HttpClient())
    {
    }

    public GoogleMailboxProfileService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<string?> GetMailboxAddressAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Information("The Google userinfo request answered {StatusCode}.", (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var document = await JsonSerializer
                .DeserializeAsync(stream, GoogleUserInfoJsonContext.Default.GoogleUserInfoResponse, timeout.Token)
                .ConfigureAwait(false);

            var email = document?.Email;
            return string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information("The Google userinfo request did not complete ({ErrorType}).", ex.GetType().Name);
            return null;
        }
    }
}

internal sealed class GoogleUserInfoResponse
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

[JsonSerializable(typeof(GoogleUserInfoResponse))]
internal partial class GoogleUserInfoJsonContext : JsonSerializerContext;
