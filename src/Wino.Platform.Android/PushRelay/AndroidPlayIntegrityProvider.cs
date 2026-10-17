#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Xamarin.Google.Android.Play.Core.Integrity;
using PlayTask = global::Android.Gms.Tasks.Task;

namespace Wino.Platform.Android.PushRelay;

/// <summary>
/// Play Integrity (classic request) for the relay device registration. The API-issued nonce goes into the request, so
/// the API can match <c>requestDetails.nonce</c>; the verdict must say the Play-distributed Wino on a genuine device.
/// Debug builds and sideloaded installs come back <c>UNRECOGNIZED_VERSION</c>, and the token provider then falls back
/// to a Wino Account. Play Integrity has no assertions; the device credential alone authenticates later calls.
/// </summary>
public sealed class AndroidPlayIntegrityProvider(Context context) : IDeviceAttestationProvider
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public DeviceAttestationKind Kind => DeviceAttestationKind.PlayIntegrity;

    /// <summary>Play services decide at request time; the provider itself is always available.</summary>
    public bool IsSupported => true;

    public async Task<DeviceAttestation> AttestAsync(string nonce, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        var manager = IntegrityManagerFactory.Create(context);
        var request = IntegrityTokenRequest.InvokeBuilder().SetNonce(nonce).Build();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var response = await AwaitAsync<IntegrityTokenResponse>(manager.RequestIntegrityToken(request), timeout.Token).ConfigureAwait(false);
        var token = response.Token();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("Play Integrity returned an empty token.");

        return new DeviceAttestation(DeviceAttestationKind.PlayIntegrity, Token: token);
    }

    public Task<string?> CreateAssertionAsync(string keyId, byte[] clientData, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    private static Task<T> AwaitAsync<T>(PlayTask task, CancellationToken cancellationToken) where T : Java.Lang.Object
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        task.AddOnSuccessListener(new SuccessListener<T>(completion));
        task.AddOnFailureListener(new FailureListener<T>(completion));
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return completion.Task;
    }

    private sealed class SuccessListener<T>(TaskCompletionSource<T> completion) : Java.Lang.Object, global::Android.Gms.Tasks.IOnSuccessListener where T : Java.Lang.Object
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            if (result is T typed) completion.TrySetResult(typed);
            else completion.TrySetException(new InvalidOperationException($"Play Integrity returned {result?.GetType().Name ?? "null"}."));
        }
    }

    private sealed class FailureListener<T>(TaskCompletionSource<T> completion) : Java.Lang.Object, global::Android.Gms.Tasks.IOnFailureListener where T : Java.Lang.Object
    {
        public void OnFailure(Java.Lang.Exception exception) => completion.TrySetException(exception);
    }
}
