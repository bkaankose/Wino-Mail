#nullable enable annotations
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Authentication.Exchange;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mapi;
using Wino.Mapi.Transport;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// Detects, with the credentials the app actually holds, which transport Autodiscover offers for an
/// account's mailbox, so setup can record it before the account exists.
/// </summary>
public sealed class MapiConnectionProbe(IExchangeAuthenticator exchangeAuthenticator) : IMapiConnectionProbe
{
    private static readonly ILogger Logger = Log.ForContext<MapiConnectionProbe>();

    public async Task<ExchangeTransport> DetectTransportAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (account.ProviderType != MailProviderType.Exchange || account.ServerInformation == null
            || !Uri.TryCreate(account.ServerInformation.IncomingServer, UriKind.Absolute, out var ewsUri))
            return ExchangeTransport.Automatic;

        try
        {
            var (credential, _) = await ResolveCredentialAsync(account).ConfigureAwait(false);
            await MapiAutodiscover.DiscoverAsync(AutodiscoverUrl(ewsUri), account.Address, credential, cancellationToken).ConfigureAwait(false);
            Logger.Information("Exchange transport for {Account}: MAPI/HTTP is advertised.", account.Address);
            return ExchangeTransport.MapiHttp;
        }
        catch (MapiNotAdvertisedException ex)
        {
            Logger.Information("Exchange transport for {Account}: EWS ({Reason}).", account.Address, ex.Message);
            return ExchangeTransport.Ews;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Exchange transport for {Account} could not be detected; leaving it undecided.", account.Address);
            return ExchangeTransport.Automatic;
        }
    }

    /// <summary>
    /// The same credential source the synchronizers use, so detection follows the app's own path.
    /// A bearer token means OAuth; otherwise the stored password as NTLM.
    /// </summary>
    private async Task<(MapiCredential Credential, string AuthMode)> ResolveCredentialAsync(MailAccount account)
    {
        var token = await exchangeAuthenticator.TryGetBearerTokenAsync(account).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
            return (new MapiCredential.Bearer(token), "OAuth bearer token");

        var credentials = await exchangeAuthenticator.GetCredentialsAsync(account).ConfigureAwait(false);
        var network = (credentials as WebCredentials)?.Credentials as NetworkCredential;
        return (new MapiCredential.Integrated("NTLM", network),
            network is null ? "Windows integrated (current identity), NTLM" : "stored password, NTLM");
    }

    private static Uri AutodiscoverUrl(Uri ewsUri) => new($"{ewsUri.Scheme}://{ewsUri.Host}/autodiscover/autodiscover.xml");
}
