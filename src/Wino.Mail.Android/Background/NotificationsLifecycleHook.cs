using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Android.Background;
using Wino.Platform.Android.Notifications;
using Wino.Platform.Android.PushRelay;

namespace Wino.Mail.Android.Background;

/// <summary>
/// Notifications and background work while the runtime runs (iOS NotificationsLifecycleHook): channels for the
/// accounts, reminders missed while the app was closed, then the in-process reminder poller, the badge count, the
/// queued-changes scheduler and the FCM token. On stop, the poller hands over to the reminder alarm.
/// </summary>
internal sealed class NotificationsLifecycleHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public async Task ServicesReadyAsync()
    {
        var builder = services.GetRequiredService<AndroidNotificationBuilder>();
        await builder.EnsureChannelsAsync();

        // The alarm covered the time the poller was not running; the poller's first window starts 30 s back.
        var reminders = services.GetRequiredService<AndroidReminderScheduler>();
        reminders.Cancel();
        try { await reminders.CatchUpAsync(AndroidReminderScheduler.BeforePollerStart(DateTime.Now)); }
        catch (Exception error) { Serilog.Log.Warning(error, "Catching up calendar reminders failed."); }
        await services.GetRequiredService<ICalendarReminderServer>().StartAsync();

        await builder.UpdateTaskbarIconBadgeAsync();
        services.GetRequiredService<AndroidPendingChangesScheduler>().Start();
        services.GetRequiredService<AndroidFcmTokenProvider>().RequestToken();
    }

    public async Task StoppingAsync(bool runtimeStarted)
    {
        if (!runtimeStarted) return;
        await services.GetRequiredService<ICalendarReminderServer>().StopAsync();
        var reminders = services.GetRequiredService<AndroidReminderScheduler>();
        reminders.Cancel();
        // The poller checked up to now; the alarm takes the next reminder from here.
        services.GetRequiredService<AndroidReminderState>().RecordChecked(DateTime.Now);
        await reminders.ScheduleNextAsync();
    }
}
