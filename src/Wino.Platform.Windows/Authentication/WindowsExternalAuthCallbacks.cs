#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Platform.Windows.Authentication;

/// <summary>
/// The redirect side of the Wino Account browser sign-in flow on Windows. <see cref="WindowsBrowserAuthBroker"/>
/// registers a wait before it opens the system browser; the <c>wino://auth-complete</c> protocol activation
/// (redirected to the running instance by single-instancing, the same path the Gmail and billing redirects use) hands
/// the address to <see cref="Deliver"/>. One flow runs at a time; the wait lives here, not in a dialog.
/// </summary>
public sealed class WindowsExternalAuthCallbacks
{
    private readonly object _gate = new();
    private TaskCompletionSource<Uri>? _pending;

    /// <summary>True while a browser sign-in is waiting for its redirect.</summary>
    public bool IsWaiting
    {
        get { lock (_gate) return _pending is { Task.IsCompleted: false }; }
    }

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
    public bool Deliver(Uri? uri)
    {
        if (!WinoAccountExternalAuth.IsCallback(uri) || uri is null) return false;

        TaskCompletionSource<Uri>? pending;
        lock (_gate) pending = _pending;
        return pending?.TrySetResult(uri) == true;
    }
}
