using System.Collections.Concurrent;
using Wino.Core.Domain.Interfaces;

namespace Wino.Hosting;

/// <summary>
/// The host's UI thread: one dedicated thread with an ordered work queue, the same contract as the WinUI and AppKit
/// dispatchers. Work runs inline on that thread and is queued from any other. The thread carries a synchronization
/// context that posts to the same queue, so ViewModel code that awaits on it continues there, as it does on a UI thread.
/// </summary>
public sealed class HostDispatcher : IDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private int _disposed;
    private int _pending;

    public HostDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Wino host UI thread"
        };
        _thread.Start();
    }

    public bool HasThreadAccess => Thread.CurrentThread == _thread;

    /// <summary>True when no queued work is waiting or running on the UI thread.</summary>
    public bool IsIdle => Volatile.Read(ref _pending) == 0;

    public Task ExecuteOnUIThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    /// <summary>Runs asynchronous work on the UI thread and completes with its result, as a page would start it.</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ExecuteOnUIThread(async () =>
        {
            try
            {
                completion.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        return completion.Task;
    }

    /// <summary>Runs asynchronous work on the UI thread and completes when it does.</summary>
    public Task InvokeAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync(async () =>
        {
            await work();
            return true;
        });
    }

    private void Post(Action action)
    {
        if (!TryPost(action))
            throw new ObjectDisposedException(nameof(HostDispatcher));
    }

    private bool TryPost(Action action)
    {
        if (_queue.IsAddingCompleted)
            return false;

        Interlocked.Increment(ref _pending);
        try
        {
            _queue.Add(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            Interlocked.Decrement(ref _pending);
            return false;
        }
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new HostSynchronizationContext(this));

        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // Work posted through the synchronization context has no task to report to; it reaches the log.
                Serilog.Log.Error(ex, "Unobserved exception on the host UI thread.");
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _queue.CompleteAdding();

        if (!HasThreadAccess)
            _thread.Join(TimeSpan.FromSeconds(10));
    }

    private sealed class HostSynchronizationContext(HostDispatcher dispatcher) : SynchronizationContext
    {
        // A continuation that arrives after the host stopped has no thread left to run on; it is dropped and logged.
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!dispatcher.TryPost(() => d(state)))
                Serilog.Log.Warning("Work posted to the host UI thread after it stopped was dropped.");
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (dispatcher.HasThreadAccess)
            {
                d(state);
                return;
            }

            dispatcher.ExecuteOnUIThread(() => d(state)).GetAwaiter().GetResult();
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
