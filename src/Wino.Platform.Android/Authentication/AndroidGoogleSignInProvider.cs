using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using AndroidX.Credentials;
using AndroidX.Credentials.Exceptions;
using Google.Android.Libraries.Identity.GoogleId;
using Wino.Core.Domain;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Platform.Android.Authentication;

/// <summary>
/// Native Google sign-in for Wino Accounts through Credential Manager's Sign in with Google option: the account picker
/// (which can add a Google account) returns an ID token issued to the API's web client
/// (<see cref="WinoAccountExternalAuth.GoogleAndroidServerClientId"/> as <c>serverClientId</c>). No Android client id is
/// named; Google matches the package name and signing certificate. The picker needs the current activity; the user
/// closing it is a cancellation, not an error.
/// </summary>
public sealed class AndroidGoogleSignInProvider(IDispatcher dispatcher, Func<Activity?> currentActivity) : IGoogleNativeSignInProvider
{
    public async Task<string?> RequestIdTokenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationSignal();

        await dispatcher.ExecuteOnUIThread(() =>
        {
            var activity = currentActivity();
            if (activity is null || activity.IsFinishing)
            {
                completion.TrySetException(new InvalidOperationException("Google sign-in requires a visible activity."));
                return;
            }

            var option = new GetSignInWithGoogleOption.Builder(WinoAccountExternalAuth.GoogleAndroidServerClientId).Build();
            var request = new GetCredentialRequest.Builder().AddCredentialOption(option).Build();
            CredentialManager.Create(activity).GetCredentialAsync(activity, request, cancellation, activity.MainExecutor!, new Callback(completion));
        }).ConfigureAwait(false);

        using var registration = cancellationToken.Register(() =>
        {
            if (completion.TrySetCanceled(cancellationToken)) cancellation.Cancel();
        });
        return await completion.Task.ConfigureAwait(false);
    }

    private sealed class Callback(TaskCompletionSource<string?> completion) : Java.Lang.Object, ICredentialManagerCallback
    {
        public void OnResult(Java.Lang.Object? result)
        {
            if (result is not GetCredentialResponse response || response.Credential is not CustomCredential credential
                || credential.Type != GoogleIdTokenCredential.TypeGoogleIdTokenCredential)
            {
                completion.TrySetException(new WinoAccountExternalSignInException(ApiErrorCodes.ExternalLoginInvalid, "Google returned no ID token credential."));
                return;
            }

            try
            {
                var idToken = GoogleIdTokenCredential.CreateFrom(credential.Data).IdToken;
                if (string.IsNullOrWhiteSpace(idToken))
                    completion.TrySetException(new WinoAccountExternalSignInException(ApiErrorCodes.ExternalLoginInvalid, "Google returned an empty ID token."));
                else
                    completion.TrySetResult(idToken);
            }
            catch (GoogleIdTokenParsingException ex)
            {
                completion.TrySetException(new WinoAccountExternalSignInException(ApiErrorCodes.ExternalLoginInvalid, ex.Message ?? "Google returned an unreadable ID token."));
            }
        }

        public void OnError(Java.Lang.Object? error)
        {
            // The callback is generic in Java; the binding hands the throwable over as a plain peer, so it is cast
            // to its managed exception type (Java.Lang.Throwable is a System.Exception, not a Java.Lang.Object).
            Java.Lang.Throwable? throwable = null;
            try { throwable = error?.JavaCast<Java.Lang.Throwable>(); }
            catch (Exception) { /* not a throwable: reported below from its text */ }

            switch (throwable)
            {
                case GetCredentialCancellationException:
                    completion.TrySetResult(null);
                    break;
                case NoCredentialException exception:
                    // No Google account on the device and the user did not add one.
                    completion.TrySetException(new WinoAccountExternalSignInException(WinoAccountClientErrorCodes.ExternalSignInFailed, exception.Message ?? "No Google account is available on this device."));
                    break;
                case GetCredentialException exception:
                    completion.TrySetException(new WinoAccountExternalSignInException(WinoAccountClientErrorCodes.ExternalSignInFailed, $"{exception.Type}: {exception.Message}"));
                    break;
                default:
                    completion.TrySetException(new WinoAccountExternalSignInException(WinoAccountClientErrorCodes.ExternalSignInFailed, error?.ToString() ?? "Google sign-in failed."));
                    break;
            }
        }
    }
}
