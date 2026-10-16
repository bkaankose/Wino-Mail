#nullable enable
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Authentication;

/// <summary>
/// Native Google sign-in for Wino Accounts on the Apple platforms: the platform's web authentication session (the same
/// <see cref="IWinoAccountBrowserAuthBroker"/> the API browser flow uses) shows Google's consent page for the Wino
/// Accounts iOS client, catches the reversed-client-id redirect, and the code is exchanged for the ID token here.
/// The user closing the session ends the attempt with <see cref="OperationCanceledException"/>, which the service
/// reports as cancelled.
/// </summary>
public sealed class GoogleWebSessionSignInProvider(IWinoAccountBrowserAuthBroker broker, HttpClient? httpClient = null, string? clientId = null) : IGoogleNativeSignInProvider
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient = httpClient ?? SharedHttpClient;
    private readonly string _clientId = clientId ?? WinoAccountExternalAuth.GoogleAppleClientId;

    public async Task<string?> RequestIdTokenAsync(CancellationToken cancellationToken)
    {
        var request = GoogleNativeSignInFlow.CreateRequest(_clientId);
        var callback = await broker.AuthorizeAsync(request.AuthorizationUri, request.RedirectUri.Scheme, cancellationToken).ConfigureAwait(false);
        try
        {
            return await GoogleNativeSignInFlow.ExchangeForIdTokenAsync(_httpClient, request, callback, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new WinoAccountExternalSignInException(WinoAccountClientErrorCodes.ExternalSignInFailed, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            throw new WinoAccountExternalSignInException(WinoAccountClientErrorCodes.ServiceUnavailable, ex.Message);
        }
    }
}
