using Android.OS;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Services;

/// <summary>
/// The UI thread is the main looper. Work already on the main thread runs inline (the AppKit and UIKit dispatcher
/// contract), so a caller on the main thread never waits for its own queue.
/// </summary>
public sealed class AndroidDispatcher : IDispatcher
{
    private readonly Looper _mainLooper;
    private readonly Handler _mainHandler;

    public AndroidDispatcher()
    {
        _mainLooper = Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable.");
        _mainHandler = new Handler(_mainLooper);
    }

    public Task ExecuteOnUIThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_mainLooper.IsCurrentThread)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = _mainHandler.Post(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception error) { completion.TrySetException(error); }
        });
        if (!posted) completion.TrySetException(new InvalidOperationException("The main looper is not running."));
        return completion.Task;
    }
}
