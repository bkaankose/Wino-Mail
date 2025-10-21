using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Mail.ViewModels.Helpers;

/// <summary>
/// A throttled event handler that delays execution of a callback until a specified time has passed
/// without the event being triggered again. This is useful for scenarios where events fire rapidly
/// but you only want to handle the "final" event after a quiet period.
/// </summary>
public class ThrottledEventHandler : IDisposable
{
    private readonly int _delayMilliseconds;
    private readonly Func<Task> _asyncCallback;
    private readonly Action _syncCallback;
    private Timer _timer;
    private volatile bool _disposed;

    /// <summary>
    /// Creates a new throttled event handler with a synchronous callback.
    /// </summary>
    /// <param name="delayMilliseconds">The delay in milliseconds to wait before executing the callback</param>
    /// <param name="callback">The action to execute after the delay period</param>
    public ThrottledEventHandler(int delayMilliseconds, Action callback)
    {
        _delayMilliseconds = delayMilliseconds;
        _syncCallback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    /// <summary>
    /// Creates a new throttled event handler with an asynchronous callback.
    /// </summary>
    /// <param name="delayMilliseconds">The delay in milliseconds to wait before executing the callback</param>
    /// <param name="callback">The async function to execute after the delay period</param>
    public ThrottledEventHandler(int delayMilliseconds, Func<Task> callback)
    {
        _delayMilliseconds = delayMilliseconds;
        _asyncCallback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    /// <summary>
    /// Triggers the throttled execution. If called again before the delay period expires,
    /// the timer is reset and the callback execution is delayed further.
    /// </summary>
    public void Trigger()
    {
        if (_disposed) return;

        // Dispose existing timer if it exists
        _timer?.Dispose();

        // Create new timer that will execute the callback after the delay
        _timer = new Timer(ExecuteCallback, null, _delayMilliseconds, Timeout.Infinite);
    }

    private async void ExecuteCallback(object state)
    {
        if (_disposed) return;

        try
        {
            if (_asyncCallback != null)
            {
                await _asyncCallback();
            }
            else
            {
                _syncCallback?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // Log error if logging is available, but don't crash
            System.Diagnostics.Debug.WriteLine($"ThrottledEventHandler callback error: {ex}");
        }
        finally
        {
            // Dispose the timer since it's one-shot
            _timer?.Dispose();
            _timer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}