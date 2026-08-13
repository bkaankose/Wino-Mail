using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class SemanticIndexJobRegistry : ISemanticIndexJobRegistry
{
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();

    public bool TryStart(Guid accountId, Func<CancellationToken, Task> worker, out Task task)
    {
        var cancellation = new CancellationTokenSource();
        var job = new Job(cancellation);
        if (!_jobs.TryAdd(accountId, job))
        {
            cancellation.Dispose();
            task = _jobs[accountId].Completion.Task;
            return false;
        }

        task = job.Completion.Task;
        _ = RunAsync(accountId, job, worker);
        return true;
    }

    public bool IsRunning(Guid accountId) => _jobs.ContainsKey(accountId);

    public async Task CancelAndWaitAsync(Guid accountId)
    {
        if (!_jobs.TryGetValue(accountId, out var job))
            return;

        job.Cancellation.Cancel();
        try
        {
            await job.Completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(Guid accountId, Job job, Func<CancellationToken, Task> worker)
    {
        try
        {
            await worker(job.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // Workers publish their own failure state; the registry only owns lifecycle.
        }
        finally
        {
            _jobs.TryRemove(new KeyValuePair<Guid, Job>(accountId, job));
            job.Cancellation.Dispose();
            job.Completion.TrySetResult();
        }
    }

    private sealed class Job(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
