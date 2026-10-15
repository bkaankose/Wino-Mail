using Android.App;
using Android.Content;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Android.Notifications;
using Wino.Services;

namespace Wino.Mail.Android.Background;

/// <summary>
/// The calendar reminder wake-up (<see cref="AndroidReminderScheduler"/>): shows the reminders that became due while
/// the in-process poller was not running and sets the alarm for the next one. While the poller runs it only moves the
/// alarm. After a reboot, an app update or a change of the exact alarm permission it sets the alarm again.
/// </summary>
[BroadcastReceiver(Name = "com.winomail.android.ReminderAlarmReceiver", Exported = false)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced, "android.app.action.SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED"])]
public sealed class ReminderAlarmReceiver : BroadcastReceiver
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(9);

    public override void OnReceive(Context? context, Intent? intent)
    {
        var action = intent?.Action ?? string.Empty;
        var pending = GoAsync();
        _ = HandleAsync(action, pending);
    }

    private static async Task HandleAsync(string action, PendingResult? pending)
    {
        try
        {
            using var budget = new CancellationTokenSource(Budget);
            if (await AndroidBackgroundRuntime.EnsureAsync(budget.Token) is not { } services) return;
            var scheduler = services.GetRequiredService<AndroidReminderScheduler>();
            var pollerRunning = services.GetRequiredService<ICalendarReminderServer>() is CalendarReminderServer { IsRunning: true };
            if (action == AndroidReminderScheduler.ActionReminderAlarm && !pollerRunning)
            {
                var shown = await scheduler.CatchUpAsync(DateTime.Now, budget.Token);
                Serilog.Log.Information("Reminder alarm showed {Count} reminder(s).", shown);
            }
            if (!pollerRunning) await scheduler.ScheduleNextAsync();
            else Serilog.Log.Information("Reminder wake-up {Action} while the poller runs; nothing to do.", action);
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Reminder wake-up {Action} failed.", action);
        }
        finally
        {
            pending?.Finish();
        }
    }
}
