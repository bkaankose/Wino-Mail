using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Mail.ViewModels;

/// <summary>Serializes accepted preference writes and retains their completion for navigation.</summary>
internal sealed class AccountSettingsWriteLifetime(Func<Exception, Task> reportFailure)
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _state = new();
    private Task _pending = Task.CompletedTask;

    internal bool HasPending { get { lock (_state) return !_pending.IsCompleted; } }

    internal void Enqueue(Func<Task> operation)
    {
        lock (_state)
        {
            var work = RunAsync(operation);
            _pending = _pending.IsCompleted ? work : Task.WhenAll(_pending, work);
        }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        await _serial.WaitAsync();
        try { await operation(); }
        catch (Exception exception) { await reportFailure(exception); }
        finally { _serial.Release(); }
    }

    internal async Task DrainAsync()
    {
        while (true)
        {
            Task pending;
            lock (_state) pending = _pending;
            await pending;
            lock (_state) { if (ReferenceEquals(pending, _pending)) return; }
        }
    }
}
