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
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DeliveryMatchTolerance = TimeSpan.FromSeconds(1);

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
                await ReplanAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
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

            var scheduled = _notificationHostClient.GetScheduled(NotificationHostApplication.Calendar);
            var removed = 0;
            foreach (var (tag, delivery) in scheduled)
            {
                if (!CalendarReminderTags.IsCalendarReminder(tag))
                    continue;

                if (wanted.TryGetValue(tag, out var entry) && (entry.Delivery - delivery).Duration() <= DeliveryMatchTolerance)
                    continue;

                _notificationHostClient.RemoveScheduled(NotificationHostApplication.Calendar, tag);
                removed++;
            }

            var kept = scheduled
                .Where(value => wanted.TryGetValue(value.Tag, out var entry) && (entry.Delivery - value.DeliveryTime).Duration() <= DeliveryMatchTolerance)
                .Select(value => value.Tag)
                .ToHashSet(StringComparer.Ordinal);

            var planned = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var added = 0;
            foreach (var (tag, entry) in wanted)
            {
                if (!kept.Contains(tag))
                {
                    // A reminder that became due while this plan was built is the poll's to show.
                    if (entry.Delivery <= DateTimeOffset.Now.AddSeconds(5))
                        continue;

                    _notificationHostClient.Schedule(NotificationHostApplication.Calendar, entry.Notification, entry.Delivery);
                    added++;
                }

                planned[tag] = entry.Delivery;
            }

            _plan.Replace(planned);

            if (added > 0 || removed > 0)
                _logger.Information("Calendar reminder toasts planned: {Added} added, {Removed} removed, {Total} ahead.", added, removed, wanted.Count);
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
