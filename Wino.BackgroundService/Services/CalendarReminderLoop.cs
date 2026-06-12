using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.BackgroundService.Services;

/// <summary>
/// Companion port of the UI CalendarReminderServer: polls due calendar reminders and
/// posts reminder toasts.
/// </summary>
public sealed partial class CalendarReminderLoop : IDisposable
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);

    private readonly ICalendarService _calendarService;
    private readonly IAccountService _accountService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly ILogger _logger = Log.ForContext<CalendarReminderLoop>();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly HashSet<string> _sentReminderKeys = [];

    private Task? _loopTask;
    private CancellationTokenSource? _loopCts;
    private DateTime _lastCheckLocal = DateTime.MinValue;

    public CalendarReminderLoop(ICalendarService calendarService, IAccountService accountService, INotificationBuilder notificationBuilder)
    {
        _calendarService = calendarService;
        _accountService = accountService;
        _notificationBuilder = notificationBuilder;
    }

    public async Task StartAsync()
    {
        await _startLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_loopTask != null)
                return;

            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            var hasCalendarAccess = accounts.Exists(a => a.IsCalendarAccessGranted);

            if (!hasCalendarAccess)
            {
                _logger.Information("Calendar reminder loop will not start because no account has calendar access.");
                return;
            }

            _lastCheckLocal = DateTime.Now.AddSeconds(-30);
            _loopCts = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_loopCts.Token);

            _logger.Information("Calendar reminder loop started.");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public void Stop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollingInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ExecuteTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Calendar reminder loop terminated unexpectedly.");
        }
    }

    private async Task ExecuteTickAsync(CancellationToken cancellationToken)
    {
        var nowLocal = DateTime.Now;

        if (_lastCheckLocal == DateTime.MinValue)
            _lastCheckLocal = nowLocal.AddSeconds(-PollingInterval.TotalSeconds);

        var dueNotifications = await _calendarService
            .CheckAndNotifyAsync(_lastCheckLocal, nowLocal, _sentReminderKeys, cancellationToken)
            .ConfigureAwait(false);

        foreach (var reminder in dueNotifications)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _notificationBuilder
                .CreateCalendarReminderNotificationAsync(reminder.CalendarItem, reminder.ReminderDurationInSeconds)
                .ConfigureAwait(false);
        }

        _lastCheckLocal = nowLocal;
    }

    public void Dispose()
    {
        Stop();
        _startLock.Dispose();
    }
}
