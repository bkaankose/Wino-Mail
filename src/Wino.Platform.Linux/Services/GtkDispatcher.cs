using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux.Services;

/// <summary>
/// The GTK main thread. Create it on that thread before the main loop runs. Work from another thread is queued on the
/// default main context in order; work from the main thread runs inline, as on every other head.
/// </summary>
public sealed class GtkDispatcher : IDispatcher
{
    private readonly int _mainThreadId = Environment.CurrentManagedThreadId;

    public bool HasThreadAccess => Environment.CurrentManagedThreadId == _mainThreadId;

    public Task ExecuteOnUIThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (HasThreadAccess)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT, () =>
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
            return false;
        });
        return completion.Task;
    }
}
