#nullable enable
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Authentication;

/// <summary>
/// Reads Microsoft's public user-realm document for an address. Managed and federated namespaces are Entra ID tenants,
/// so their mailboxes are Microsoft 365 even when the MX record points at a mail filter.
/// </summary>
public sealed class MicrosoftUserRealmService : IMicrosoftUserRealmService
{
    private const string UserRealmUrlFormat = "https://login.microsoftonline.com/common/userrealm/{0}?api-version=2.0";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly ILogger Logger = Log.ForContext<MicrosoftUserRealmService>();

    private readonly HttpClient _httpClient;

    public MicrosoftUserRealmService() : this(new HttpClient())
    {
    }

    public MicrosoftUserRealmService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<MicrosoftUserRealm> GetUserRealmAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        var address = emailAddress?.Trim() ?? string.Empty;
        if (address.Length == 0 || address.IndexOf('@') <= 0)
            return MicrosoftUserRealm.Unknown;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            var url = string.Format(UserRealmUrlFormat, Uri.EscapeDataString(address));
            using var response = await _httpClient.GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Information("The Microsoft user-realm lookup answered {StatusCode}.", (int)response.StatusCode);
                return MicrosoftUserRealm.Unknown;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var document = await JsonSerializer
                .DeserializeAsync(stream, MicrosoftUserRealmJsonContext.Default.MicrosoftUserRealmResponse, timeout.Token)
                .ConfigureAwait(false);

            return document is null ? MicrosoftUserRealm.Unknown : new MicrosoftUserRealm(
                ParseNamespaceType(document.NameSpaceType),
                NullIfEmpty(document.DomainName),
                NullIfEmpty(document.FederationBrandName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information("The Microsoft user-realm lookup did not complete ({ErrorType}).", ex.GetType().Name);
            return MicrosoftUserRealm.Unknown;
        }
    }

    private static MicrosoftNamespaceType ParseNamespaceType(string? value)
    {
        if (string.Equals(value, "Managed", StringComparison.OrdinalIgnoreCase))
            return MicrosoftNamespaceType.Managed;

        if (string.Equals(value, "Federated", StringComparison.OrdinalIgnoreCase))
            return MicrosoftNamespaceType.Federated;

        return MicrosoftNamespaceType.Unknown;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class MicrosoftUserRealmResponse
{
    [JsonPropertyName("NameSpaceType")]
    public string? NameSpaceType { get; set; }

    [JsonPropertyName("DomainName")]
    public string? DomainName { get; set; }

    [JsonPropertyName("FederationBrandName")]
    public string? FederationBrandName { get; set; }
}

[JsonSerializable(typeof(MicrosoftUserRealmResponse))]
internal partial class MicrosoftUserRealmJsonContext : JsonSerializerContext;
