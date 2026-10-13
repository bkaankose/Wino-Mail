using CoreFoundation;
using Foundation;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// The UI thread is the main queue. Work already on the main thread runs inline (the Mac
/// AppKitDispatcher contract), so a caller on the main thread never waits for its own queue.
/// </summary>
public sealed class UIKitDispatcher : IDispatcher
{
    public Task ExecuteOnUIThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (NSThread.IsMain)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }
}
