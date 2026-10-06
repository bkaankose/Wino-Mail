using AppKit;
using Foundation;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.MacOS.Infrastructure;

public sealed class AppKitDispatcher : IDispatcher
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
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        return completion.Task;
    }
}
