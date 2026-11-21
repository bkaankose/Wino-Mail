using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Calendar;
using Wino.NotificationHost.Contracts;
using Wino.Platform.Windows.Services;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// The calendar reminders Windows was asked to show on its own, by toast tag and delivery time. The in-process
/// reminder poll skips a reminder that is in this plan, so a reminder shows once whether Wino runs or not.
/// </summary>
public sealed class CalendarReminderToastPlan
{
    private static readonly TimeSpan DeliveryTolerance = TimeSpan.FromMinutes(5);
    private readonly Lock _lock = new();
    private Dictionary<string, DateTimeOffset> _planned = new(StringComparer.Ordinal);

    public void Replace(IReadOnlyDictionary<string, DateTimeOffset> planned)
    {
        lock (_lock)
            _planned = new Dictionary<string, DateTimeOffset>(planned, StringComparer.Ordinal);
    }

    public void Remove(string tag)
    {
        lock (_lock)
            _planned.Remove(tag);
    }

    /// <summary>True when Windows shows the toast with <paramref name="tag"/> around <paramref name="now"/>.</summary>
    public bool IsDeliveredBySchedule(string tag, DateTimeOffset now)
    {
        lock (_lock)
            return _planned.TryGetValue(tag, out var delivery) && (delivery - now).Duration() <= DeliveryTolerance;
    }
}

/// <summary>
/// Hands the calendar reminders of the next seven days to Windows as scheduled toasts under the Calendar
/// notification identity, so they fire while Wino is closed. Activating one goes through the notification
/// host like any other toast. The plan is rebuilt when Wino starts and after calendar items change;
/// quiet hours and notification settings are evaluated for the moment each reminder fires.
/// </summary>
public sealed class CalendarReminderToastScheduler :
    IRecipient<CalendarItemAdded>,
    IRecipient<CalendarItemUpdated>,
    IRecipient<CalendarItemDeleted>
{
    private static readonly TimeSpan PlanningWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan PlanningLead = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(1);

    // Windows keeps a limited number of scheduled toasts per application.
    private const int MaximumScheduledReminders = 200;

    private readonly ICalendarService _calendarService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly INotificationHostClient _notificationHostClient;
    private readonly CalendarReminderToastPlan _plan;
    private readonly IMessenger _messenger;
    private readonly ILogger _logger = Log.ForContext<CalendarReminderToastScheduler>();
    private readonly SemaphoreSlim _planLock = new(1, 1);
    private CancellationTokenSource? _pendingReplan;
    private int _isStarted;
    private (int Count, DateTimeOffset? Next) _lastLoggedPlan = (-1, null);

    public CalendarReminderToastScheduler(
        ICalendarService calendarService,
        INotificationBuilder notificationBuilder,
        INotificationHostClient notificationHostClient,
        CalendarReminderToastPlan plan,
        IMessenger messenger)
    {
        _calendarService = calendarService;
        _notificationBuilder = notificationBuilder;
        _notificationHostClient = notificationHostClient;
        _plan = plan;
        _messenger = messenger;
    }

    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _isStarted, 1) == 0)
        {
            _messenger.Register<CalendarItemAdded>(this);
            _messenger.Register<CalendarItemUpdated>(this);
            _messenger.Register<CalendarItemDeleted>(this);
        }

        return ReplanAsync();
    }

    public void Receive(CalendarItemAdded message) => QueueReplan();
    public void Receive(CalendarItemUpdated message) => QueueReplan();
    public void Receive(CalendarItemDeleted message) => QueueReplan();

    private void QueueReplan()
    {
        var pending = new CancellationTokenSource();
        Interlocked.Exchange(ref _pendingReplan, pending)?.Cancel();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ChangeDebounce, pending.Token).ConfigureAwait(false);
                if (Interlocked.CompareExchange(ref _pendingReplan, null, pending) == pending)
                    await ReplanAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>
    /// Runs a replan that is still waiting for its debounce, so a change made right before Wino exits
    /// reaches Windows before the process ends.
    /// </summary>
    public async Task FlushPendingAsync(TimeSpan timeout)
    {
        var pending = Interlocked.Exchange(ref _pendingReplan, null);
        if (pending == null || pending.IsCancellationRequested)
            return;

        pending.Cancel();
        await Task.WhenAny(ReplanAsync(), Task.Delay(timeout)).ConfigureAwait(false);
    }

    public async Task ReplanAsync()
    {
        if (_notificationBuilder is not NotificationBuilder builder)
            return;

        await _planLock.WaitAsync().ConfigureAwait(false);

        try
        {
            var now = DateTime.Now;
            var upcoming = await _calendarService
                .GetUpcomingReminderTriggersAsync(now + PlanningLead, now + PlanningWindow, MaximumScheduledReminders)
                .ConfigureAwait(false);

            var wanted = new Dictionary<string, (Microsoft.Windows.AppNotifications.AppNotification Notification, DateTimeOffset Delivery)>(StringComparer.Ordinal);
            foreach (var reminder in upcoming)
            {
                var delivery = new DateTimeOffset(DateTime.SpecifyKind(reminder.TriggerLocal, DateTimeKind.Local));
                var notification = await builder
                    .BuildScheduledCalendarReminderAsync(reminder.CalendarItem, reminder.ReminderDurationInSeconds, reminder.TriggerLocal)
                    .ConfigureAwait(false);

                if (notification != null)
                    wanted.TryAdd(notification.Tag, (notification, delivery));
            }

            // Every planned toast is replaced, so a renamed or moved event never fires with its old text or time.
            var removed = 0;
            foreach (var (tag, _) in _notificationHostClient.GetScheduled(NotificationHostApplication.Calendar))
            {
                if (!CalendarReminderTags.IsCalendarReminder(tag))
                    continue;

                _notificationHostClient.RemoveScheduled(NotificationHostApplication.Calendar, tag);
                removed++;
            }

            var planned = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            Exception? scheduleFailure = null;
            foreach (var (tag, entry) in wanted)
            {
                // A reminder that became due while this plan was built is the poll's to show.
                if (entry.Delivery <= DateTimeOffset.Now.AddSeconds(5))
                    continue;

                try
                {
                    _notificationHostClient.Schedule(NotificationHostApplication.Calendar, entry.Notification, entry.Delivery);
                    planned[tag] = entry.Delivery;
                }
                catch (Exception ex)
                {
                    scheduleFailure ??= ex;
                }
            }

            _plan.Replace(planned);

            if (scheduleFailure != null)
                _logger.Warning(scheduleFailure, "Windows refused {Count} of {Total} calendar reminder toasts; the in-process poll delivers them while Wino runs.", wanted.Count - planned.Count, wanted.Count);

            var signature = (planned.Count, Next: planned.Count == 0 ? (DateTimeOffset?)null : planned.Values.Min());
            if (signature != _lastLoggedPlan)
            {
                _lastLoggedPlan = signature;
                _logger.Information("Calendar reminder toasts planned with Windows: {Planned} ahead ({Removed} replaced), next at {Next}.", planned.Count, removed, signature.Next);
            }
        }
        catch (Exception ex)
        {
            // The in-process poll still delivers reminders while Wino runs.
            _logger.Warning(ex, "Could not plan calendar reminder toasts.");
        }
        finally
        {
            _planLock.Release();
        }
    }
}

public static class CalendarReminderTags
{
    private const string Prefix = "calendar-reminder-";

    public static string Create(Guid calendarItemId, long reminderDurationInSeconds)
        => $"{Prefix}{calendarItemId:N}-{reminderDurationInSeconds}";

    public static bool IsCalendarReminder(string tag) => tag.StartsWith(Prefix, StringComparison.Ordinal);
}
