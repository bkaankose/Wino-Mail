using System.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Intelligence.ConsoleApp.Hosting;
using Wino.Messaging.UI;

namespace Wino.Intelligence.ConsoleApp.Scenarios;

/// <summary>
/// Follows the coordinator's MailIntelligenceJobChanged snapshots for one account, the same feed
/// the management page binds to. It prints every visible change with a timestamp and records
/// when each milestone was first reached, so a run ends with a phase table.
/// </summary>
internal sealed class JobMonitor : IDisposable
{
    private readonly Guid _accountId;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly List<(string Milestone, TimeSpan At)> _milestones = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<MailIntelligenceJobSnapshot> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _lastLine;

    public JobMonitor(Guid accountId)
    {
        _accountId = accountId;
        WeakReferenceMessenger.Default.Register<JobMonitor, MailIntelligenceJobChanged>(this,
            static (monitor, message) => monitor.OnChanged(message));
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public MailIntelligenceJobSnapshot? Last { get; private set; }

    public static bool IsTerminal(MailIntelligenceJobStatus status) => status is
        MailIntelligenceJobStatus.Completed or
        MailIntelligenceJobStatus.Failed or
        MailIntelligenceJobStatus.Cancelled or
        MailIntelligenceJobStatus.PausedForQuota;

    public void Mark(string milestone)
    {
        lock (_gate)
        {
            if (_seen.Add(milestone))
                _milestones.Add((milestone, _clock.Elapsed));
        }
    }

    /// <summary>Waits for a terminal snapshot. Returns null on timeout.</summary>
    public async Task<MailIntelligenceJobSnapshot?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _finished.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public void PrintMilestones()
    {
        lock (_gate)
        {
            ConsoleOutput.Header("Phase timeline");
            var previous = TimeSpan.Zero;
            foreach (var (milestone, at) in _milestones)
            {
                ConsoleOutput.Info($"  {at:mm\\:ss\\.fff}  +{ConsoleOutput.Elapsed(at - previous),-10} {milestone}");
                previous = at;
            }

            ConsoleOutput.Info($"  {_clock.Elapsed:mm\\:ss\\.fff}  total");
        }
    }

    private void OnChanged(MailIntelligenceJobChanged message)
    {
        if (message.AccountId != _accountId)
            return;

        var snapshot = message.Snapshot;
        lock (_gate)
        {
            Last = snapshot;
            Record($"status {snapshot.Status}");
            RecordStage("classification", snapshot.Classification);
            RecordStage("enrichment", snapshot.Enrichment);

            var line = $"{snapshot.Status,-12} processed {snapshot.ProcessedMessageCount}/{snapshot.SelectedMessageCount}" +
                       $" failed {snapshot.FailedMessageCount} jobs {snapshot.ActiveJobCount}" +
                       $" | classification {Describe(snapshot.Classification)} | enrichment {Describe(snapshot.Enrichment)}" +
                       (snapshot.ErrorCode is { Length: > 0 } error ? $" | error {error}" : string.Empty);

            if (line != _lastLine)
            {
                _lastLine = line;
                var color = snapshot.Status switch
                {
                    MailIntelligenceJobStatus.Completed => ConsoleColor.Green,
                    MailIntelligenceJobStatus.Failed or MailIntelligenceJobStatus.Cancelled => ConsoleColor.Red,
                    MailIntelligenceJobStatus.PausedForQuota => ConsoleColor.Yellow,
                    _ when snapshot.ErrorCode is { Length: > 0 } => ConsoleColor.Yellow,
                    _ => ConsoleColor.Cyan,
                };
                ConsoleOutput.Timeline(_clock, line, color);
            }
        }

        if (IsTerminal(snapshot.Status))
            _finished.TrySetResult(snapshot);
    }

    private void RecordStage(string name, MailIntelligenceStageProgress stage)
    {
        if (stage.Status is { Length: > 0 } status && status != "pending")
            Record($"{name} {status}");
        if (stage.IsImported)
            Record($"{name} imported ({stage.PageCount} page(s))");
        if (stage.IsAcknowledged)
            Record($"{name} acknowledged");
    }

    private void Record(string milestone)
    {
        if (_seen.Add(milestone))
            _milestones.Add((milestone, _clock.Elapsed));
    }

    private static string Describe(MailIntelligenceStageProgress stage)
        => $"{stage.Status}{(stage.PageCount > 0 ? $" p{stage.PageCount}" : string.Empty)}" +
           $"{(stage.IsImported ? " imported" : string.Empty)}{(stage.IsAcknowledged ? " acked" : string.Empty)}";

    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}
