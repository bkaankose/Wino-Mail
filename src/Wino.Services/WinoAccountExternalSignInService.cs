#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Services;

/// <summary>
/// Wino Account sign-in with Apple and Google. A provider signs in natively when the head registered its native half
/// (Apple's sheet through AuthenticationServices; a Google ID token from a web session or Credential Manager), and
/// otherwise through the API browser flow when a <see cref="IWinoAccountBrowserAuthBroker"/> exists: the broker shows
/// <c>api/v1/auth/{provider}/authorize</c> and returns the <see cref="WinoAccountExternalAuth.CallbackUri"/> redirect,
/// whose completion code finishes the sign-in (bounded by <see cref="WinoAccountExternalAuth.CompletionCodeLifetime"/>).
/// One provider sheet runs at a time; a second tap waits for the first.
/// </summary>
public sealed class WinoAccountExternalSignInService(
    IWinoAccountApiClient apiClient,
    IWinoAccountProfileService profileService,
    IAppleNativeSignInProvider? apple = null,
    IGoogleNativeSignInProvider? google = null,
    IWinoAccountBrowserAuthBroker? broker = null) : IWinoAccountExternalSignInService
{
    private readonly ILogger _logger = Log.ForContext<WinoAccountExternalSignInService>();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How long the browser wait lasts; tests shorten it.</summary>
    internal TimeSpan BrowserTimeout { get; init; } = WinoAccountExternalAuth.CompletionCodeLifetime;

    public bool IsAppleAvailable => apple is not null || broker is not null;

    public bool IsGoogleAvailable => google is not null || broker is not null;

    public Task<WinoAccountOperationResult> SignInWithAppleAsync(CancellationToken cancellationToken = default)
        => RunAsync(WinoAccountExternalAuth.AppleProvider, apple is null ? null : async token =>
        {
            var credential = await apple.RequestCredentialAsync(token).ConfigureAwait(false);
            return credential is null ? null : await profileService.SignInWithAppleAsync(credential, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task<WinoAccountOperationResult> SignInWithGoogleAsync(CancellationToken cancellationToken = default)
        => RunAsync(WinoAccountExternalAuth.GoogleProvider, google is null ? null : async token =>
        {
            var idToken = await google.RequestIdTokenAsync(token).ConfigureAwait(false);
            return idToken is null ? null : await profileService.SignInWithGoogleAsync(idToken, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <param name="native">The native path; a null result means the user dismissed the provider's UI.</param>
    private async Task<WinoAccountOperationResult> RunAsync(string provider, Func<CancellationToken, Task<WinoAccountOperationResult?>>? native, CancellationToken cancellationToken)
    {
        if (native is null && broker is null)
            return WinoAccountOperationResult.Failure(WinoAccountClientErrorCodes.ExternalSignInFailed);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = native is not null
                ? await native(cancellationToken).ConfigureAwait(false)
                : await BrowserSignInAsync(provider, cancellationToken).ConfigureAwait(false);
            return result ?? WinoAccountOperationResult.Failure(WinoAccountClientErrorCodes.SignInCancelled);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The user closed the provider's sheet or browser surface.
            return WinoAccountOperationResult.Failure(WinoAccountClientErrorCodes.SignInCancelled);
        }
        catch (WinoAccountExternalSignInException ex)
        {
            _logger.Warning("The {Provider} sign-in failed on the device: {ErrorCode} {Message}", provider, ex.ErrorCode, ex.Message);
            return WinoAccountOperationResult.Failure(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "The {Provider} sign-in could not be shown.", provider);
            return WinoAccountOperationResult.Failure(WinoAccountClientErrorCodes.ExternalSignInFailed, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WinoAccountOperationResult?> BrowserSignInAsync(string provider, CancellationToken cancellationToken)
    {
        if (broker is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BrowserTimeout);
        Uri callback;
        try
        {
            callback = await broker.AuthorizeAsync(apiClient.GetExternalSignInAuthorizationUri(provider), WinoAccountExternalAuth.CallbackScheme, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The user closed the browser surface, or the completion code would have expired anyway.
            return null;
        }

        var result = WinoAccountExternalAuth.ParseCallback(callback);
        if (result.ErrorCode is not null)
        {
            _logger.Warning("The {Provider} browser sign-in ended with {ErrorCode}.", provider, result.ErrorCode);
            return WinoAccountOperationResult.Failure(result.ErrorCode);
        }
        if (result.Code is null)
        {
            _logger.Warning("The {Provider} browser sign-in returned no completion code.", provider);
            return WinoAccountOperationResult.Failure(ApiErrorCodes.ExternalAuthCodeInvalid);
        }

        return await profileService.CompleteExternalSignInAsync(result.Code, cancellationToken).ConfigureAwait(false);
    }
}
