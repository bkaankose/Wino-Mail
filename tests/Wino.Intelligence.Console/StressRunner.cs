using System.Text;
using System.Text.Json;

namespace Wino.Intelligence.ConsoleApp;

internal sealed class StressRunner(StressOptions options, IServiceProvider services, Uri target, string runId)
{
    private readonly List<StressIntervalSnapshot> _intervals = [];
    private readonly List<StressPhaseSummary> _phases = [];
    private double? _lastHealthyRps;
    private double? _firstUnhealthyRps;
    private double? _sustainableRps;
    private StressStopReason _stopReason = StressStopReason.Completed;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputFolder);
        var started = DateTimeOffset.UtcNow;
        PrintConfiguration();
        var workload = await StressWorkload.CreateAsync(options, services, runId, cancellationToken).ConfigureAwait(false);
        var engine = new StressLoadEngine(options.MaxConcurrency, runId);

        try
        {
            if (options.Profile == StressProfile.Ai)
            {
                await RunPhaseAsync(engine, workload, "ai-capped", options.StartRps,
                    EstimateAiDuration(), cancellationToken).ConfigureAwait(false);
                _stopReason = workload.AiRequests >= options.AiRequestLimit ? StressStopReason.AiRequestLimit : StressStopReason.Completed;
            }
            else
            {
                await RunPhaseAsync(engine, workload, "warmup", 1, TimeSpan.FromMinutes(5), cancellationToken, measured: false)
                    .ConfigureAwait(false);
                await RunCapacitySequenceAsync(engine, workload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _stopReason = StressStopReason.Cancelled;
        }
        finally
        {
            await WriteReportAsync(new StressReport(runId, target, MaskAccount(options.Account), options.Profile, started,
                DateTimeOffset.UtcNow, _stopReason, _lastHealthyRps, _firstUnhealthyRps, _sustainableRps,
                workload.AiRequests, _phases)).ConfigureAwait(false);
        }
        return _stopReason is StressStopReason.Completed or StressStopReason.AiRequestLimit ? 0 : 1;
    }

    private async Task RunCapacitySequenceAsync(StressLoadEngine engine, StressWorkload workload,
        CancellationToken cancellationToken)
    {
        var rate = options.StartRps;
        while (rate <= options.MaxRps)
        {
            var healthy = await RunPhaseAsync(engine, workload, $"ramp-{rate:0.##}", rate,
                options.StageDuration, cancellationToken).ConfigureAwait(false);
            if (healthy) _lastHealthyRps = rate;
            else
            {
                _firstUnhealthyRps = rate;
                break;
            }
            if (rate == options.MaxRps) break;
            rate = Math.Min(options.MaxRps, rate * 2);
        }

        if (_lastHealthyRps is null)
        {
            _stopReason = StressStopReason.ErrorThreshold;
            return;
        }

        if (_firstUnhealthyRps is { } unhealthy && unhealthy - _lastHealthyRps.Value > 1)
        {
            var refinement = Math.Round((_lastHealthyRps.Value + unhealthy) / 2d, 2);
            if (await RunPhaseAsync(engine, workload, $"refine-{refinement:0.##}", refinement,
                    options.StageDuration, cancellationToken).ConfigureAwait(false))
                _lastHealthyRps = refinement;
            else
                _firstUnhealthyRps = refinement;
        }

        _sustainableRps = _lastHealthyRps;
        if (!await RunPhaseAsync(engine, workload, "sustain", _sustainableRps.Value,
                options.SustainDuration, cancellationToken).ConfigureAwait(false))
        {
            _stopReason = StressStopReason.ErrorThreshold;
            return;
        }

        await RunPhaseAsync(engine, workload, "spike", Math.Min(options.MaxRps, _sustainableRps.Value * 2),
            TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        await RunPhaseAsync(engine, workload, "recovery", _sustainableRps.Value,
            TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RunPhaseAsync(StressLoadEngine engine, StressWorkload workload, string name,
        double rps, TimeSpan duration, CancellationToken cancellationToken, bool measured = true)
    {
        ConsoleOutput.Header($"\n{name}: {rps:0.##} RPS for {duration:g}");
        var severeIntervals = 0;
        var saturatedIntervals = 0;
        bool StopAfterInterval(StressIntervalSnapshot snapshot)
        {
            severeIntervals = snapshot.FailurePercentage > 5 ? severeIntervals + 1 : 0;
            saturatedIntervals = snapshot.Backlogged > 0 ? saturatedIntervals + 1 : 0;
            var authenticationFailures = snapshot.Failures.GetValueOrDefault(nameof(StressFailureKind.Authentication));
            if (authenticationFailures > Math.Max(10, snapshot.Completed / 2))
                _stopReason = StressStopReason.AuthenticationFailure;
            else if (severeIntervals >= 3)
                _stopReason = StressStopReason.ErrorThreshold;
            else if (saturatedIntervals >= 3)
                _stopReason = StressStopReason.ConcurrencySaturation;
            return _stopReason != StressStopReason.Completed;
        }
        var snapshots = await engine.RunPhaseAsync(name, rps, duration,
            (sequence, token) => workload.ExecuteAsync(sequence, name, token), cancellationToken,
            options.Profile == StressProfile.Ai ? options.AiRequestLimit : null, StopAfterInterval).ConfigureAwait(false);
        if (measured)
        {
            _intervals.AddRange(snapshots);
            await AppendIntervalsAsync(snapshots).ConfigureAwait(false);
        }

        var completed = snapshots.Sum(static x => x.Completed);
        var successful = snapshots.Sum(static x => x.Successful);
        var backlogged = snapshots.Sum(static x => x.Backlogged);
        var p95 = StressPercentiles.Calculate(snapshots.Select(static x => x.P95Ms).Order().ToArray(), .95);
        var failurePercentage = completed == 0 ? 100 : (completed - successful) * 100d / completed;
        var consecutiveUnhealthy = 0;
        var hasTwoConsecutiveUnhealthy = false;
        foreach (var snapshot in snapshots)
        {
            consecutiveUnhealthy = snapshot.IsHealthy ? 0 : consecutiveUnhealthy + 1;
            if (consecutiveUnhealthy >= 2) hasTwoConsecutiveUnhealthy = true;
        }
        var healthy = !hasTwoConsecutiveUnhealthy && failurePercentage < 1 && p95 < 2_000 && backlogged == 0;
        if (measured)
            _phases.Add(new StressPhaseSummary(name, rps, snapshots[0].StartedAtUtc, snapshots[^1].EndedAtUtc,
                healthy, completed, successful, backlogged, p95, failurePercentage));

        ConsoleOutput.Status($"{name}: {completed:N0} completed, p95 {p95:N0} ms, " +
            $"failures {failurePercentage:0.##}%, backlog {backlogged:N0}",
            healthy ? Wino.Core.Domain.Models.SemanticIndexing.SemanticIndexJobStatus.Completed :
                Wino.Core.Domain.Models.SemanticIndexing.SemanticIndexJobStatus.Failed);
        return healthy && _stopReason == StressStopReason.Completed;
    }

    private TimeSpan EstimateAiDuration()
        => TimeSpan.FromSeconds(Math.Max(1, options.AiRequestLimit!.Value / options.StartRps));

    private async Task AppendIntervalsAsync(IEnumerable<StressIntervalSnapshot> snapshots)
    {
        var path = Path.Combine(options.OutputFolder, $"stress-{runId}-intervals.jsonl");
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, true);
        await using var writer = new StreamWriter(stream);
        foreach (var snapshot in snapshots)
            await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot)).ConfigureAwait(false);
    }

    private async Task WriteReportAsync(StressReport report)
    {
        var basePath = Path.Combine(options.OutputFolder, $"stress-{runId}-report");
        await File.WriteAllTextAsync(basePath + ".json", JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
        await File.WriteAllTextAsync(basePath + ".md", BuildMarkdown(report)).ConfigureAwait(false);
        ConsoleOutput.Success($"Reports written to {basePath}.json and {basePath}.md");
    }

    internal static string BuildMarkdown(StressReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Wino Intelligence stress report").AppendLine()
            .AppendLine($"- Run ID: `{report.RunId}`")
            .AppendLine($"- Target: `{report.Target.GetLeftPart(UriPartial.Authority)}`")
            .AppendLine($"- Account: `{MaskAccount(report.Account)}`")
            .AppendLine($"- Profile: `{report.Profile}`")
            .AppendLine($"- Window: `{report.StartedAtUtc:O}` to `{report.EndedAtUtc:O}`")
            .AppendLine($"- Stop reason: `{report.StopReason}`")
            .AppendLine($"- Last healthy rate: `{report.LastHealthyRps?.ToString("0.##") ?? "none"} RPS`")
            .AppendLine($"- First unhealthy rate: `{report.FirstUnhealthyRps?.ToString("0.##") ?? "none"} RPS`")
            .AppendLine($"- Sustainable rate: `{report.SustainableRps?.ToString("0.##") ?? "none"} RPS`")
            .AppendLine($"- AI requests: `{report.AiRequests}`").AppendLine()
            .AppendLine("## Phases").AppendLine()
            .AppendLine("| Phase | Target RPS | Completed | p95 ms | Failures | Backlog | Healthy |")
            .AppendLine("|---|---:|---:|---:|---:|---:|:---:|");
        foreach (var phase in report.Phases)
            builder.AppendLine($"| {phase.Name} | {phase.TargetRps:0.##} | {phase.Completed} | {phase.P95Ms:0} | {phase.FailurePercentage:0.##}% | {phase.Backlogged} | {(phase.Healthy ? "yes" : "no")} |");
        builder.AppendLine().AppendLine("## Correlate server telemetry").AppendLine()
            .AppendLine("Use the run ID and UTC window above in Application Insights. Compare degradation with App Service CPU, memory, queueing, connections, restarts, and 5xx responses; VM CPU credits, memory, disk, and network; PostgreSQL connections, waits, cache, temporary I/O, and vector-query latency; and SQL Server CPU, waits, blocking, deadlocks, and top queries.")
            .AppendLine().AppendLine("Do not attribute the limit to the database tier unless client degradation aligns with database saturation or waits. Check App Service, upstream AI throttling, authentication/quota, and network limits first.");
        return builder.ToString();
    }

    internal static string MaskAccount(string account)
    {
        var at = account.IndexOf('@');
        return at <= 1 ? "***" : $"{account[0]}***{account[(at - 1)..]}";
    }

    private void PrintConfiguration()
    {
        if (options.Environment == ApiEnvironment.Production)
            ConsoleOutput.Warning("PRODUCTION STRESS TEST — supervise this run and watch Azure/database telemetry.");
        System.Console.WriteLine($"Run ID: {runId}");
        System.Console.WriteLine($"Target: {target}");
        System.Console.WriteLine($"Account: {options.Account}");
        System.Console.WriteLine($"Profile: {options.Profile}");
        System.Console.WriteLine($"RPS: {options.StartRps:0.##} to {options.MaxRps:0.##}; concurrency: {options.MaxConcurrency}");
        System.Console.WriteLine($"Stage: {options.StageDuration:g}; sustain: {options.SustainDuration:g}; output: {options.OutputFolder}");
        System.Console.WriteLine(options.Profile == StressProfile.Ai
            ? $"Phases: capped AI run ({options.AiRequestLimit} requests)"
            : "Phases: 5-minute warm-up, doubling ramp, boundary refinement, sustain, 2x spike, recovery");
    }
}
