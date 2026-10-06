namespace Wino.Editor;

/// <summary>Serializes session mutations without releasing the queue while a canceled native operation still runs.</summary>
public sealed class SessionOperationQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposing;
    private Task? _disposal;

    public Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default) =>
        RunAsync(_ => operation(), cancellationToken);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposing();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await _gate.WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfDisposing();
            throw;
        }
        try
        {
            ThrowIfDisposing();
            linked.Token.ThrowIfCancellationRequested();
            T result = await operation(linked.Token);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfDisposing();
            throw;
        }
        finally { _gate.Release(); }
    }

    public Task RunAsync(Func<Task> operation, CancellationToken cancellationToken = default) =>
        RunAsync(async () => { await operation(); return true; }, cancellationToken);

    public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
        RunAsync(async token => { await operation(token); return true; }, cancellationToken);

    public ValueTask DisposeAsync(Func<ValueTask> dispose)
    {
        TaskCompletionSource completion;
        lock (_stateGate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposal = completion.Task;
        }
        _lifetime.Cancel();
        _ = DisposeCoreAsync(dispose, completion);
        return new(completion.Task);
    }

    private void ThrowIfDisposing()
    {
        lock (_stateGate) ObjectDisposedException.ThrowIf(_disposing, this);
    }

    private async Task DisposeCoreAsync(Func<ValueTask> dispose, TaskCompletionSource completion)
    {
        try
        {
            await _gate.WaitAsync();
            try { await dispose(); }
            finally { _gate.Release(); }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}
