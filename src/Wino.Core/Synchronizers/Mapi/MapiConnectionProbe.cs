#nullable enable annotations
using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Authentication.Exchange;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mapi;
using Wino.Mapi.Transport;

namespace Wino.Core.Synchronizers.Mapi;

/// <summary>
/// Proves the native MAPI/HTTP path to an account's mailbox from inside the app, with the credentials
/// the app actually holds (the setup page's "Test MAPI" and the account details card), and detects
/// which transport Autodiscover offers so setup can record it before the account exists.
/// </summary>
public sealed class MapiConnectionProbe(IExchangeAuthenticator exchangeAuthenticator) : IMapiConnectionProbe
{
    private static readonly ILogger Logger = Log.ForContext<MapiConnectionProbe>();
    private const string UserAgent = "WinoMail/MAPI";

    public async Task<MapiProbeResult> ProbeAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        if (account.ProviderType != MailProviderType.Exchange || account.ServerInformation == null)
            return new MapiProbeResult(false, "Account", "Only on-premises Exchange accounts have a MAPI/HTTP endpoint.");

        if (!Uri.TryCreate(account.ServerInformation.IncomingServer, UriKind.Absolute, out var ewsUri))
            return new MapiProbeResult(false, "Account", "The account's Exchange server URL is not a valid absolute URL.");

        var stopwatch = Stopwatch.StartNew();

        MapiCredential credential;
        string authMode;
        try
        {
            (credential, authMode) = await ResolveCredentialAsync(account).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI probe: could not obtain credentials for {Account}.", account.Address);
            return new MapiProbeResult(false, "Credentials", ex.Message);
        }

        // Autodiscover: the routable endpoint (with MailboxId) and the legacyExchangeDN both come from it.
        MapiEndpointInfo endpoint;
        try
        {
            endpoint = await MapiAutodiscover.DiscoverAsync(AutodiscoverUrl(ewsUri), account.Address, credential, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI probe: Autodiscover failed for {Account}.", account.Address);
            return new MapiProbeResult(false, "Autodiscover", ex.Message, AuthMode: authMode);
        }

        // Connect + Logon. Transport trace lines carry sizes and status only; they go to Debug.
        var transport = new MapiHttpTransport(endpoint.MailStoreUrl, credential, UserAgent, line => Logger.Debug("MAPI probe {Line}", line));
        var stage = "Connect";
        try
        {
            await using var session = await MapiSession.OpenAsync(transport, endpoint.LegacyDn, cancellationToken).ConfigureAwait(false);
            stage = "Logon";
            var logon = session.Logon!;

            stopwatch.Stop();
            Logger.Information("MAPI probe succeeded for {Account} via {AuthMode} in {Elapsed} ms.", account.Address, authMode, stopwatch.ElapsedMilliseconds);

            return new MapiProbeResult(
                true,
                "Logon",
                "Connected and logged on.",
                session.DisplayName ?? account.Address,
                authMode,
                Redact(endpoint.MailStoreUrl),
                endpoint.LegacyDn,
                logon.FolderIds.Length,
                stopwatch.ElapsedMilliseconds);
        }
        catch (MapiConnectException ex)
        {
            Logger.Warning(ex, "MAPI probe: Connect refused for {Account}.", account.Address);
            return new MapiProbeResult(false, "Connect", ex.Message, AuthMode: authMode, Endpoint: Redact(endpoint.MailStoreUrl));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "MAPI probe: {Stage} failed for {Account}.", stage, account.Address);
            return new MapiProbeResult(false, stage, ex.Message, AuthMode: authMode, Endpoint: Redact(endpoint.MailStoreUrl));
        }
    }

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
    /// The same credential source the synchronizers use, so the probe proves the app's own path.
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

    /// <summary>The MailboxId in the query is a mailbox GUID; show the host and path, not the id.</summary>
    private static string Redact(Uri url) => $"{url.Scheme}://{url.Host}{url.AbsolutePath}?MailboxId=...";
}
