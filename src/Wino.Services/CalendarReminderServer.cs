#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class CalendarReminderServer : ICalendarReminderServer
{
    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(30);

    private readonly ICalendarService _calendarService;
    private readonly IAccountService _accountService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly TimeSpan _pollingInterval;
    private readonly ILogger _logger = Log.ForContext<CalendarReminderServer>();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly HashSet<string> _sentReminderKeys = [];

    private Task? _loopTask;
    private CancellationTokenSource? _loopCts;
    private DateTime _lastCheckLocal = DateTime.MinValue;

    public CalendarReminderServer(ICalendarService calendarService, IAccountService accountService, INotificationBuilder notificationBuilder)
        : this(calendarService, accountService, notificationBuilder, DefaultPollingInterval)
    {
    }

    /// <summary>Creates a server with a custom polling interval. Tests use a short interval.</summary>
    internal CalendarReminderServer(ICalendarService calendarService, IAccountService accountService, INotificationBuilder notificationBuilder, TimeSpan pollingInterval)
    {
        _calendarService = calendarService;
        _accountService = accountService;
        _notificationBuilder = notificationBuilder;
        _pollingInterval = pollingInterval;
    }

    /// <summary>Whether the polling loop is running.</summary>
    public bool IsRunning => _loopTask != null;

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
                _logger.Information("Calendar reminder server will not start because no account has calendar access.");
                return;
            }

            _lastCheckLocal = DateTime.Now.AddSeconds(-_pollingInterval.TotalSeconds);
            _loopCts = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_loopCts.Token);

            _logger.Information("Calendar reminder server started.");
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
                // no-op
            }

            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;

            _logger.Information("Calendar reminder server stopped.");
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollingInterval);

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
            _logger.Error(ex, "Calendar reminder server loop terminated unexpectedly.");
        }
    }

    private async Task ExecuteTickAsync(CancellationToken cancellationToken)
    {
        var nowLocal = DateTime.Now;

        if (_lastCheckLocal == DateTime.MinValue)
            _lastCheckLocal = nowLocal.AddSeconds(-_pollingInterval.TotalSeconds);

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
}
