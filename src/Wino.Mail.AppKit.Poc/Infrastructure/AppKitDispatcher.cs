using AppKit;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.AppKit.Poc.Infrastructure;

internal sealed class AppKitDispatcher : IDispatcher
{
    public Task ExecuteOnUIThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });

        return completion.Task;
    }
}
