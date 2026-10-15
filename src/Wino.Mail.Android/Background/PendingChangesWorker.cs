using Android.Content;
using Android.Runtime;
using AndroidX.Core.App;
using AndroidX.Work;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Android.Notifications;

namespace Wino.Mail.Android.Background;

/// <summary>
/// The queued-changes flush (<see cref="Wino.Platform.Android.Background.AndroidPendingChangesScheduler"/>): runs
/// <see cref="IApplicationRuntime.FlushQueuedRequestsAsync"/> and asks to run again later while changes are still
/// queued (a server that does not answer). The queues live in memory, so in a process that was restarted for this work
/// there is nothing to send and the work ends at once. The Java name is fixed because WorkManager stores it.
/// </summary>
[Register("com.winomail.android.PendingChangesWorker")]
public sealed class PendingChangesWorker : Worker
{
    /// <summary>Below WorkManager's 10-minute limit, so the result is ours and not a timeout.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(8);
    private const int ForegroundNotificationId = 0x57494E4F;

    public PendingChangesWorker(Context context, WorkerParameters workerParameters) : base(context, workerParameters) { }

    public override Result DoWork()
    {
        try
        {
            using var budget = new CancellationTokenSource(Budget);
            var flushed = Task.Run(async () =>
            {
                if (await AndroidBackgroundRuntime.EnsureAsync(budget.Token) is not { } services) return true;
                return await services.GetRequiredService<IApplicationRuntime>().FlushQueuedRequestsAsync(budget.Token);
            }).GetAwaiter().GetResult();
            Serilog.Log.Information("Queued changes flush finished (attempt {Attempt}): {State}.", RunAttemptCount + 1, flushed ? "nothing left" : "changes still queued");
            return flushed ? Result.InvokeSuccess() : Result.InvokeRetry();
        }
        catch (Exception error)
        {
            // A stopped runtime (the process is ending) or a cancelled pass: try again in a later run.
            Serilog.Log.Warning(error, "Queued changes flush failed.");
            return Result.InvokeRetry();
        }
    }

    /// <summary>Below API 31 expedited work runs as a foreground service, which needs this silent notification.</summary>
    public override ForegroundInfo ForegroundInfo
    {
        get
        {
            var context = ApplicationContext;
            AndroidNotificationChannels.EnsureFixed(context);
            var notification = new NotificationCompat.Builder(context, AndroidNotificationChannels.Background)
                .SetSmallIcon(Wino.Platform.Android.Resource.Drawable.wino_notification_sync)
                .SetContentTitle(Translator.Android_Notification_SendingChanges)
                .SetPriority(NotificationCompat.PriorityMin)
                .SetOngoing(true)
                .SetSilent(true)
                .Build();
            return OperatingSystem.IsAndroidVersionAtLeast(29)
                ? new ForegroundInfo(ForegroundNotificationId, notification, (int)global::Android.Content.PM.ForegroundService.TypeDataSync)
                : new ForegroundInfo(ForegroundNotificationId, notification);
        }
    }
}
