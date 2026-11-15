#nullable enable
using System;
using System.Threading;
using Microsoft.Exchange.WebServices.Data;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.MAPI;
using Wino.Core.MAPI.Transport;
using Wino.Core.Synchronizers.Mapi;
using Task = System.Threading.Tasks.Task;

namespace Wino.Core.Services;

/// <summary>
/// Signs in to Autodiscover first, which needs the credential and also reports whether MAPI over HTTP is offered, and
/// binds the mailbox root over EWS when Autodiscover cannot answer or the account is pinned to EWS.
/// </summary>
public sealed class ExchangeConnectionTester(IExchangeAuthenticator exchangeAuthenticator) : IExchangeConnectionTester
{
    private static readonly ILogger Logger = Log.ForContext<ExchangeConnectionTester>();

    public async System.Threading.Tasks.Task<ExchangeConnectionTestResult> TestAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var serverInformation = account.ServerInformation;
        if (serverInformation == null ||
            !Uri.TryCreate(serverInformation.IncomingServer?.Trim(), UriKind.Absolute, out var ewsUri) ||
            ewsUri.Scheme != Uri.UriSchemeHttps)
        {
            return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.Failed, FailureReason: Translator.ExchangeSetup_ServerAddressInvalid);
        }

        var detected = ExchangeTransport.Automatic;

        if (serverInformation.ExchangeTransport != ExchangeTransport.Ews)
        {
            try
            {
                var mapiCredential = await MapiConnectionProbe.ToMapiCredentialAsync(exchangeAuthenticator, account, cancellationToken).ConfigureAwait(false);
                await MapiAutodiscover.DiscoverAsync(MapiConnectionProbe.AutodiscoverUrl(ewsUri), account.Address, mapiCredential, cancellationToken).ConfigureAwait(false);
                return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.Succeeded, ExchangeTransport.MapiHttp);
            }
            catch (MapiNotAdvertisedException)
            {
                detected = ExchangeTransport.Ews;
            }
            catch (MapiTransportException ex) when (ex.IsUnauthorized)
            {
                return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.CredentialRejected);
            }
            catch (AuthenticationAttentionException)
            {
                return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.CredentialRejected);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Information("Exchange Autodiscover did not answer the connection check ({ErrorType}); checking EWS instead.", ex.GetType().Name);
            }
        }

        try
        {
            var credential = await exchangeAuthenticator.GetCredentialAsync(account, cancellationToken).ConfigureAwait(false);
            var service = new ExchangeService(ExchangeVersion.Exchange2013_SP1)
            {
                Credentials = ToEwsCredentials(credential),
                Url = ewsUri
            };

            if (!string.IsNullOrEmpty(account.Address))
                service.HttpHeaders["X-AnchorMailbox"] = account.Address;

            cancellationToken.ThrowIfCancellationRequested();
            await Folder.Bind(service, WellKnownFolderName.MsgFolderRoot, new PropertySet(BasePropertySet.IdOnly)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.Succeeded, detected);
        }
        catch (AuthenticationAttentionException)
        {
            return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.CredentialRejected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (IsUnauthorized(ex))
                return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.CredentialRejected);

            Logger.Warning(ex, "The Exchange connection check against {Host} failed.", ewsUri.Host);
            return new ExchangeConnectionTestResult(ExchangeConnectionTestStatus.Failed, detected, ex.Message);
        }
    }

    private static ExchangeCredentials ToEwsCredentials(ExchangeCredential credential) => credential switch
    {
        ExchangeCredential.Password password => new WebCredentials(password.Credential),
        ExchangeCredential.Bearer bearer => new OAuthCredentials(bearer.AccessToken),
        _ => throw new InvalidOperationException($"Unsupported Exchange credential {credential.GetType().Name}.")
    };

    // The EWS client reports a refused credential only in its message text.
    private static bool IsUnauthorized(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            var message = current.Message ?? string.Empty;
            if (message.Contains("(401)", StringComparison.Ordinal) ||
                message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
