using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using UserNotifications;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Calendar;
using Wino.Platform.Apple.Services;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Hands the upcoming calendar reminders to the notification center, so they fire while Wino is closed (Windows
/// CalendarReminderToastScheduler). The window is replanned when calendar items change. A reminder that is already due
/// is left to the live reminder server, which shows it under the same identifier, so it is never shown twice.
/// </summary>
public sealed class MacCalendarReminderScheduler :
    IRecipient<CalendarItemAdded>,
    IRecipient<CalendarItemUpdated>,
    IRecipient<CalendarItemDeleted>
{
    private static readonly TimeSpan PlanningWindow = TimeSpan.FromDays(7);
    private static readonly TimeSpan PlanningLead = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(1);
    private const int MaximumScheduledReminders = 200;
    private const string CalendarReminderPrefix = "calendar-reminder-";
    private static readonly TimeSpan PendingListTimeout = TimeSpan.FromSeconds(10);

    private readonly ICalendarService _calendarService;
    private readonly AppleNotificationBuilder? _builder;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _planLock = new(1, 1);
    private CancellationTokenSource? _pendingReplan;
    private int _isStarted;

    public MacCalendarReminderScheduler(ICalendarService calendarService, INotificationBuilder notificationBuilder, IMessenger messenger)
    {
        _calendarService = calendarService;
        _builder = notificationBuilder as AppleNotificationBuilder;
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

    public async Task ReplanAsync()
    {
        if (_builder is null) return;

        await _planLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var now = DateTime.Now;
            var upcoming = await _calendarService
                .GetUpcomingReminderTriggersAsync(now + PlanningLead, now + PlanningWindow, MaximumScheduledReminders)
                .ConfigureAwait(false);

            var wanted = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var reminder in upcoming)
            {
                if (reminder.TriggerLocal <= now + PlanningLead) continue;

                wanted.Add(AppleNotificationBuilder.CalendarReminderIdentifier(reminder.CalendarItem, reminder.ReminderDurationInSeconds));
                await _builder.ScheduleCalendarReminderAsync(reminder.CalendarItem, reminder.ReminderDurationInSeconds, reminder.TriggerLocal).ConfigureAwait(false);
            }

            var removed = 0;
            var pending = await PendingCalendarRemindersAsync().ConfigureAwait(false);
            if (pending is null)
            {
                Serilog.Log.Warning("The notification center did not list its pending requests; stale calendar reminders are not removed.");
            }
            else
            {
                var stale = pending.Where(identifier => !wanted.Contains(identifier)).ToArray();
                if (stale.Length > 0)
                    UNUserNotificationCenter.Current.RemovePendingNotificationRequests(stale);
                removed = stale.Length;
            }

            Serilog.Log.Information("Calendar reminders planned with the notification center: {Planned} ahead, {Removed} stale removed.", wanted.Count, removed);
        }
        catch (Exception exception)
        {
            // The live reminder server still shows reminders while Wino runs.
            Serilog.Log.Warning(exception, "Could not plan calendar reminders with the notification center.");
        }
        finally
        {
            _planLock.Release();
        }
    }

    private static async Task<string[]?> PendingCalendarRemindersAsync()
    {
        var completion = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        UNUserNotificationCenter.Current.GetPendingNotificationRequests(requests => completion.TrySetResult(requests
            .Where(request => request.Trigger is UNCalendarNotificationTrigger && request.Identifier.StartsWith(CalendarReminderPrefix, StringComparison.Ordinal))
            .Select(request => request.Identifier)
            .ToArray()));
        var finished = await Task.WhenAny(completion.Task, Task.Delay(PendingListTimeout)).ConfigureAwait(false);
        return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
    }
}
