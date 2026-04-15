using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;

namespace Wino.Mail.WinUI.Activation;

internal sealed record BufferedAppNotificationActivation(string Argument, IReadOnlyDictionary<string, string>? UserInput);

internal sealed class AppNotificationActivationBuffer
{
    private readonly ConcurrentQueue<BufferedAppNotificationActivation> _pendingActivations = new();
    private readonly SemaphoreSlim _pendingSignal = new(0);

    public void Enqueue(AppNotificationActivatedEventArgs args)
    {
        var copiedUserInput = args.UserInput == null
            ? null
            : new Dictionary<string, string>(args.UserInput, StringComparer.Ordinal);

        _pendingActivations.Enqueue(new BufferedAppNotificationActivation(args.Argument, copiedUserInput));
        _pendingSignal.Release();
    }

    public bool TryDequeue(out BufferedAppNotificationActivation activation)
        => _pendingActivations.TryDequeue(out activation!);

    public async Task<BufferedAppNotificationActivation?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_pendingActivations.IsEmpty && TryDequeue(out var queuedActivation))
            return queuedActivation;

        if (!await _pendingSignal.WaitAsync(timeout, cancellationToken))
            return null;

        return TryDequeue(out var activation) ? activation : null;
    }
}
