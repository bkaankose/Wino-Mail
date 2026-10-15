using Android.App;
using Android.Content;
using AndroidX.Core.App;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Notifications;

/// <summary>
/// What the reminder wake-ups need to survive the process: the end of the last window that was checked for due
/// reminders, and when a reminder notification was last shown. Kept in a private SharedPreferences file.
/// </summary>
public sealed class AndroidReminderState(Context context)
{
    private const string FileName = "wino_reminders";
    private const string LastCheckKey = "last_check_local_ticks";
    private const string LastShownKey = "last_shown_local_ticks";

    private ISharedPreferences Preferences => context.GetSharedPreferences(FileName, FileCreationMode.Private)!;

    /// <summary>
    /// The start of the next catch-up window: the later of the last checked window and the last shown reminder (the
    /// in-process poller shows reminders in trigger order, so earlier triggers were already handled). Null before the first check.
    /// </summary>
    public DateTime? HandledUntilLocal
    {
        get
        {
            var ticks = Math.Max(Preferences.GetLong(LastCheckKey, 0), Preferences.GetLong(LastShownKey, 0));
            return ticks > 0 ? new DateTime(ticks, DateTimeKind.Local) : null;
        }
    }

    public void RecordChecked(DateTime untilLocal) => Write(LastCheckKey, untilLocal);

    public void RecordShown(DateTime atLocal) => Write(LastShownKey, atLocal);

    private void Write(string key, DateTime value)
    {
        using var editor = Preferences.Edit()!;
        editor.PutLong(key, value.Ticks);
        editor.Apply();
    }
}

/// <summary>
/// Wakes the app for the next calendar reminder while the in-process poller (<see cref="ICalendarReminderServer"/>)
/// is not running: AlarmManager, inexact by default, exact when the user allows exact alarms
/// (SCHEDULE_EXACT_ALARM, <c>canScheduleExactAlarms</c> on API 31+). When is a reminder due stays the shared rule
/// (<see cref="ICalendarService.GetNextReminderTriggerAsync"/> and <see cref="ICalendarService.CheckAndNotifyAsync"/>).
/// </summary>
public sealed class AndroidReminderScheduler(Context context, Type alarmReceiverType, ICalendarService calendarService,
    INotificationBuilder notificationBuilder, AndroidReminderState state)
{
    public const string ActionReminderAlarm = "com.winomail.android.action.REMINDER_ALARM";

    /// <summary>The in-process poller's first window starts this long before it starts.</summary>
    private static readonly TimeSpan PollerLookBack = TimeSpan.FromSeconds(30);

    /// <summary>Whether exact alarms are allowed (always below API 31).</summary>
    public bool CanScheduleExactAlarms
        => context.GetSystemService(Context.AlarmService) is AlarmManager manager && AlarmManagerCompat.CanScheduleExactAlarms(manager);

    /// <summary>The time the next alarm is set for, for diagnostics; null when none is set.</summary>
    public DateTime? ScheduledForLocal { get; private set; }

    /// <summary>Whether <see cref="ScheduledForLocal"/> is an exact alarm.</summary>
    public bool IsScheduledExact { get; private set; }

    /// <summary>Sets the alarm for the next reminder after now, or cancels it when none is ahead.</summary>
    public async Task ScheduleNextAsync()
    {
        var next = await calendarService.GetNextReminderTriggerAsync(DateTime.Now).ConfigureAwait(false);
        if (context.GetSystemService(Context.AlarmService) is not AlarmManager manager) return;
        var operation = CreateOperation();
        if (next is not { } trigger)
        {
            manager.Cancel(operation);
            ScheduledForLocal = null;
            Serilog.Log.Information("No calendar reminder ahead; the reminder alarm is cleared.");
            return;
        }

        var atMillis = new DateTimeOffset(DateTime.SpecifyKind(trigger, DateTimeKind.Local)).ToUnixTimeMilliseconds();
        var exact = AlarmManagerCompat.CanScheduleExactAlarms(manager);
        if (exact) AlarmManagerCompat.SetExactAndAllowWhileIdle(manager, (int)AlarmType.RtcWakeup, atMillis, operation);
        else AlarmManagerCompat.SetAndAllowWhileIdle(manager, (int)AlarmType.RtcWakeup, atMillis, operation);
        ScheduledForLocal = trigger;
        IsScheduledExact = exact;
        Serilog.Log.Information("Calendar reminder alarm set for {Trigger} ({Kind}).", trigger, exact ? "exact" : "inexact");
    }

    /// <summary>Cancels the alarm (the in-process poller takes over while the runtime runs).</summary>
    public void Cancel()
    {
        if (context.GetSystemService(Context.AlarmService) is AlarmManager manager) manager.Cancel(CreateOperation());
        ScheduledForLocal = null;
    }

    /// <summary>
    /// Shows the reminders that became due since the last handled time and up to <paramref name="untilLocal"/>
    /// (now for an alarm; just before the poller's first window when the runtime starts), then records the window.
    /// </summary>
    public async Task<int> CatchUpAsync(DateTime untilLocal, CancellationToken cancellationToken = default)
    {
        // A first run has nothing to catch up: reminders before it were never the app's to show.
        var from = state.HandledUntilLocal ?? untilLocal;
        var shown = 0;
        if (untilLocal > from)
        {
            var due = await calendarService.CheckAndNotifyAsync(from, untilLocal, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            foreach (var reminder in due)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await notificationBuilder.CreateCalendarReminderNotificationAsync(reminder.CalendarItem, reminder.ReminderDurationInSeconds).ConfigureAwait(false);
                shown++;
            }
        }
        state.RecordChecked(untilLocal);
        return shown;
    }

    /// <summary>The end of the catch-up window when the in-process poller is about to start.</summary>
    public static DateTime BeforePollerStart(DateTime nowLocal) => nowLocal - PollerLookBack;

    private PendingIntent CreateOperation()
    {
        var intent = new Intent(context, Java.Lang.Class.FromType(alarmReceiverType));
        intent.SetAction(ActionReminderAlarm);
        return PendingIntent.GetBroadcast(context, 0, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }
}
