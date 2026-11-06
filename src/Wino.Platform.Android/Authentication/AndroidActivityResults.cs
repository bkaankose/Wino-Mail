using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Microsoft.Identity.Client;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Authentication;

/// <summary>The result an activity started for a result returned.</summary>
public sealed record AndroidActivityResult(Result ResultCode, Intent? Data);

/// <summary>
/// Starts system sign-in screens (Play services consent) for a result from the head's current activity and completes
/// the waiting task when the head forwards <c>Activity.OnActivityResult</c> to <see cref="OnActivityResult"/>. Results
/// with a request code this class did not issue go to MSAL, which starts its own browser activity for a result.
/// Pending requests live here, not in the activity, so a result delivered to a recreated activity still completes them.
/// </summary>
public sealed class AndroidActivityResults(IDispatcher dispatcher, Func<Activity?> currentActivity)
{
    // MSAL uses request code 0 for the browser and its broker code 1001; stay clear of both and of 16-bit limits.
    private const int FirstRequestCode = 0x5100;
    private const int LastRequestCode = 0x51FF;

    private readonly ConcurrentDictionary<int, TaskCompletionSource<AndroidActivityResult>> _pending = new();
    private int _nextRequestCode = FirstRequestCode;

    /// <summary>The current activity read on the main thread, or null while the app has none.</summary>
    public async Task<Activity?> GetCurrentActivityAsync()
    {
        Activity? activity = null;
        await dispatcher.ExecuteOnUIThread(() => activity = currentActivity()).ConfigureAwait(false);
        return activity;
    }

    /// <summary>
    /// Starts <paramref name="intentSender"/> for a result. Throws <see cref="InvalidOperationException"/> when no activity
    /// is available; <paramref name="cancellationToken"/> abandons the wait (the screen stays until the user closes it).
    /// </summary>
    public async Task<AndroidActivityResult> StartIntentSenderAsync(IntentSender intentSender, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intentSender);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<AndroidActivityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCode = ReserveRequestCode(completion);
        try
        {
            await dispatcher.ExecuteOnUIThread(() =>
            {
                var activity = currentActivity()
                    ?? throw new InvalidOperationException("Interactive sign-in requires a visible activity.");
                ActivityCompat.StartIntentSenderForResult(activity, intentSender, requestCode, null, 0, 0, 0, null);
            }).ConfigureAwait(false);

            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestCode, out _);
        }
    }

    /// <summary>
    /// Starts <paramref name="intent"/> (a system picker such as ACTION_OPEN_DOCUMENT or the photo picker) for a result.
    /// Throws <see cref="InvalidOperationException"/> when no activity is available.
    /// </summary>
    public async Task<AndroidActivityResult> StartActivityForResultAsync(Intent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<AndroidActivityResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCode = ReserveRequestCode(completion);
        try
        {
            await dispatcher.ExecuteOnUIThread(() =>
            {
                var activity = currentActivity()
                    ?? throw new InvalidOperationException("Picking files requires a visible activity.");
                ActivityCompat.StartActivityForResult(activity, intent, requestCode, null);
            }).ConfigureAwait(false);

            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(requestCode, out _);
        }
    }

    /// <summary>Called by the head from <c>Activity.OnActivityResult</c> on the main thread.</summary>
    public void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (_pending.TryRemove(requestCode, out var completion))
        {
            completion.TrySetResult(new AndroidActivityResult(resultCode, data));
            return;
        }

        // MSAL's browser activity (system browser sign-in) returns here; MSAL ignores results it is not waiting for.
        AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(requestCode, resultCode, data);
    }

    private int ReserveRequestCode(TaskCompletionSource<AndroidActivityResult> completion)
    {
        for (var attempt = 0; attempt <= LastRequestCode - FirstRequestCode; attempt++)
        {
            var code = Interlocked.Increment(ref _nextRequestCode);
            code = FirstRequestCode + ((code - FirstRequestCode) % (LastRequestCode - FirstRequestCode + 1));
            if (_pending.TryAdd(code, completion)) return code;
        }
        throw new InvalidOperationException("Too many sign-in screens are waiting for a result.");
    }
}
