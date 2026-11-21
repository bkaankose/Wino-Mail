#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

/// <summary>
/// Polls the task table like the calendar reminder server polls events, and hands each reminder that
/// fell due since the last tick to <see cref="INotificationBuilder.CreateTaskReminderNotificationAsync"/>.
/// A task's reminder time and its snooze time both fire; the poll window keeps either from firing twice.
/// </summary>
public sealed class TaskReminderServer(ITaskQueryService taskQueryService, IPreferencesService preferencesService, INotificationBuilder notificationBuilder) : ITaskReminderServer
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger = Log.ForContext<TaskReminderServer>();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly HashSet<string> _sentKeys = [];

    private Task? _loopTask;
    private CancellationTokenSource? _loopCts;
    private DateTime _lastCheckUtc;

    public async Task StartAsync()
    {
        await _startLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_loopTask != null)
                return;

            _lastCheckUtc = DateTime.UtcNow - PollingInterval;
            _loopCts = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_loopCts.Token);
            _logger.Information("Task reminder server started.");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _startLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_loopTask == null)
                return;

            _loopCts?.Cancel();

            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
            _logger.Information("Task reminder server stopped.");
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollingInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error(ex, "Task reminder check failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var fromUtc = _lastCheckUtc;
        _lastCheckUtc = nowUtc;

        if (!preferencesService.AreTaskRemindersEnabled)
            return;

        var dueTasks = await taskQueryService.GetTasksWithRemindersAsync(fromUtc, nowUtc).ConfigureAwait(false);
        foreach (var task in dueTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var firesAt = task.ReminderSnoozedUntilUtc is { } snoozed && snoozed > fromUtc && snoozed <= nowUtc
                ? snoozed
                : task.ReminderAtUtc;
            if (!_sentKeys.Add($"{task.Id:N}-{firesAt?.Ticks}"))
                continue;

            await notificationBuilder.CreateTaskReminderNotificationAsync(task).ConfigureAwait(false);
        }
    }
}
