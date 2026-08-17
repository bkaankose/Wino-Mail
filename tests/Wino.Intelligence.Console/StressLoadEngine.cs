using System.Collections.Concurrent;
using System.Diagnostics;

namespace Wino.Intelligence.ConsoleApp;

internal sealed class StressLoadEngine(int maxConcurrency, string runId)
{
    private readonly SemaphoreSlim _slots = new(maxConcurrency, maxConcurrency);
    private readonly ConcurrentQueue<StressOperationResult> _results = new();
    private int _inFlight;
    private long _scheduled;
    private long _started;
    private long _backlogged;

    public async Task<IReadOnlyList<StressIntervalSnapshot>> RunPhaseAsync(
        string phase,
        double targetRps,
        TimeSpan duration,
        Func<long, CancellationToken, Task<StressOperationResult>> execute,
        CancellationToken cancellationToken,
        long? maximumScheduled = null,
        Func<StressIntervalSnapshot, bool>? stopAfterInterval = null)
    {
        var snapshots = new List<StressIntervalSnapshot>();
        var active = new ConcurrentDictionary<long, Task>();
        var phaseStarted = DateTimeOffset.UtcNow;
        var intervalStarted = phaseStarted;
        var stopwatch = Stopwatch.StartNew();
        var interval = TimeSpan.FromSeconds(1d / targetRps);
        var next = TimeSpan.Zero;
        long sequence = 0;

        while (stopwatch.Elapsed < duration && !cancellationToken.IsCancellationRequested &&
               (maximumScheduled is null || sequence < maximumScheduled.Value))
        {
            var wait = next - stopwatch.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

            var id = sequence++;
            Interlocked.Increment(ref _scheduled);
            if (!_slots.Wait(0))
            {
                Interlocked.Increment(ref _backlogged);
            }
            else
            {
                Interlocked.Increment(ref _started);
                Interlocked.Increment(ref _inFlight);
                var task = ExecuteAndReleaseAsync(id, execute, cancellationToken);
                active[id] = task;
                _ = task.ContinueWith(_ => active.TryRemove(id, out _), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            next += interval;
            var now = DateTimeOffset.UtcNow;
            if (now - intervalStarted >= TimeSpan.FromMinutes(1))
            {
                var snapshot = CreateSnapshot(phase, intervalStarted, now, targetRps);
                snapshots.Add(snapshot);
                intervalStarted = now;
                if (stopAfterInterval?.Invoke(snapshot) == true) break;
            }
        }

        await Task.WhenAll(active.Values).ConfigureAwait(false);
        var ended = DateTimeOffset.UtcNow;
        if (ended > intervalStarted || snapshots.Count == 0)
            snapshots.Add(CreateSnapshot(phase, intervalStarted, ended, targetRps));
        return snapshots;
    }

    private async Task ExecuteAndReleaseAsync(long id, Func<long, CancellationToken, Task<StressOperationResult>> execute,
        CancellationToken cancellationToken)
    {
        try { _results.Enqueue(await execute(id, cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
            _slots.Release();
        }
    }

    private StressIntervalSnapshot CreateSnapshot(string phase, DateTimeOffset started, DateTimeOffset ended, double targetRps)
    {
        var results = new List<StressOperationResult>();
        while (_results.TryDequeue(out var result)) results.Add(result);
        var scheduled = Interlocked.Exchange(ref _scheduled, 0);
        var startedCount = Interlocked.Exchange(ref _started, 0);
        var backlogged = Interlocked.Exchange(ref _backlogged, 0);
        var seconds = Math.Max(0.001, (ended - started).TotalSeconds);
        var successful = results.LongCount(static x => x.Success);
        var latencies = results.Select(static x => x.DurationMs).Order().ToArray();
        var failures = results.Where(static x => !x.Success)
            .GroupBy(static x => x.FailureKind.ToString()).ToDictionary(static x => x.Key, static x => (long)x.Count());
        var statusCodes = results.SelectMany(static x => x.AttemptStatusCodes ??
                (x.StatusCode.HasValue ? new[] { x.StatusCode.Value } : Array.Empty<int>()))
            .GroupBy(static x => x.ToString()).ToDictionary(static x => x.Key, static x => (long)x.Count());
        var routes = results.GroupBy(static x => x.Route).ToDictionary(static x => x.Key, CreateRouteSnapshot);
        return new StressIntervalSnapshot(runId, phase, started, ended, targetRps, scheduled, startedCount,
            results.Count, successful, backlogged, Volatile.Read(ref _inFlight), results.Count / seconds,
            successful / seconds, results.Count == 0 ? 0 : (results.Count - successful) * 100d / results.Count,
            StressPercentiles.Calculate(latencies, .50), StressPercentiles.Calculate(latencies, .90),
            StressPercentiles.Calculate(latencies, .95), StressPercentiles.Calculate(latencies, .99),
            latencies.LastOrDefault(), results.Sum(static x => x.RequestBytes), results.Sum(static x => x.ResponseBytes),
            statusCodes, results.Sum(static x => (long)Math.Max(1, x.AttemptCount)),
            results.LongCount(static x => !string.IsNullOrWhiteSpace(x.RetryAfter)), failures, routes);
    }

    private static StressRouteSnapshot CreateRouteSnapshot(IGrouping<string, StressOperationResult> group)
    {
        var items = group.ToArray();
        var latencies = items.Select(static x => x.DurationMs).Order().ToArray();
        var failures = items.Where(static x => !x.Success).GroupBy(static x => x.FailureKind.ToString())
            .ToDictionary(static x => x.Key, static x => (long)x.Count());
        var statusCodes = items.Where(static x => x.StatusCode.HasValue).GroupBy(static x => x.StatusCode!.Value.ToString())
            .ToDictionary(static x => x.Key, static x => (long)x.Count());
        return new StressRouteSnapshot(items.Length, items.LongCount(static x => x.Success),
            StressPercentiles.Calculate(latencies, .50), StressPercentiles.Calculate(latencies, .95),
            StressPercentiles.Calculate(latencies, .99), latencies.LastOrDefault(), statusCodes, failures);
    }
}
