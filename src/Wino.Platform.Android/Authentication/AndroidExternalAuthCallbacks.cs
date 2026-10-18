using Wino.Core.Domain.Models.Accounts;
using AndroidUri = Android.Net.Uri;

namespace Wino.Platform.Android.Authentication;

/// <summary>
/// The redirect side of the Wino Account browser sign-in flow. <see cref="AndroidBrowserAuthBroker"/> registers a wait
/// before it opens the Custom Tab; the head's redirect activity (the <c>wino://auth-complete</c> intent filter)
/// hands the address to <see cref="Deliver"/>. One flow runs at a time; the wait lives here, not in an activity, so a
/// redirect delivered after a configuration change still completes it.
/// </summary>
public sealed class AndroidExternalAuthCallbacks
{
    private readonly object _gate = new();
    private TaskCompletionSource<Uri>? _pending;

    /// <summary>Waits for the next callback address. Throws <see cref="InvalidOperationException"/> while another wait is pending.</summary>
    public async Task<Uri> WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending is { Task.IsCompleted: false })
                throw new InvalidOperationException("A browser sign-in is already waiting for its redirect.");
            _pending = completion;
        }

        try
        {
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, completion)) _pending = null;
            }
        }
    }

    /// <summary>Completes the pending wait with <paramref name="uri"/>; false when it is not the callback or nothing waits.</summary>
    public bool Deliver(AndroidUri? uri)
    {
        var text = uri?.ToString();
        if (text is null || !Uri.TryCreate(text, UriKind.Absolute, out var callback) || !WinoAccountExternalAuth.IsCallback(callback))
            return false;

        TaskCompletionSource<Uri>? pending;
        lock (_gate) pending = _pending;
        return pending?.TrySetResult(callback) == true;
    }
}
