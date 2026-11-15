#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.MAPI;
using Wino.Core.MAPI.Transport;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// Detects which transport Autodiscover offers for an account's mailbox with the credentials the app holds, so setup
/// can record it before the account exists.
/// </summary>
public sealed class MapiConnectionProbe(IExchangeAuthenticator exchangeAuthenticator) : IMapiConnectionProbe
{
    private static readonly ILogger Logger = Log.ForContext<MapiConnectionProbe>();

    public async Task<ExchangeTransport> DetectTransportAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (account.ProviderType != MailProviderType.Exchange
            || account.ServerInformation == null
            || !Uri.TryCreate(account.ServerInformation.IncomingServer, UriKind.Absolute, out var ewsUri))
            return ExchangeTransport.Automatic;

        try
        {
            var credential = await ToMapiCredentialAsync(exchangeAuthenticator, account, cancellationToken).ConfigureAwait(false);
            await MapiAutodiscover.DiscoverAsync(AutodiscoverUrl(ewsUri), account.Address, credential, cancellationToken).ConfigureAwait(false);
            Logger.Information("Exchange transport for {Account}: MAPI over HTTP is advertised.", account.Address);
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
    /// The account's Exchange credential for MAPI over HTTP: a bearer token that is re-minted through the
    /// authenticator when the server rejects it, or the stored password over NTLM.
    /// </summary>
    internal static async Task<MapiCredential> ToMapiCredentialAsync(IExchangeAuthenticator authenticator, MailAccount account, CancellationToken cancellationToken)
    {
        var credential = await authenticator.GetCredentialAsync(account, cancellationToken).ConfigureAwait(false);

        return credential switch
        {
            ExchangeCredential.Bearer bearer => new MapiCredential.Bearer(bearer.AccessToken, token => authenticator.TryGetBearerTokenAsync(account, token)),
            // Exchange offers Negotiate and NTLM; .NET would pick Negotiate and not fall back when Kerberos cannot work.
            ExchangeCredential.Password password => new MapiCredential.Integrated("NTLM", password.Credential),
            _ => throw new InvalidOperationException($"Unsupported Exchange credential {credential.GetType().Name}.")
        };
    }

    /// <summary>The POX Autodiscover endpoint beside the account's Exchange server URL.</summary>
    internal static Uri AutodiscoverUrl(Uri ewsUri) => new($"{ewsUri.Scheme}://{ewsUri.Host}/autodiscover/autodiscover.xml");
}
