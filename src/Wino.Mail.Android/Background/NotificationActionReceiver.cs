using Android.App;
using Android.Content;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Android.Notifications;

namespace Wino.Mail.Android.Background;

/// <summary>
/// Notification actions that need no UI (the configured mail actions such as Mark read, Delete and Archive, inline
/// reply, Snooze, Dismiss). The response goes to the shared <see cref="INotificationResponseService"/>; a mail action
/// queues a change request, which schedules the queued-changes flush.
/// </summary>
[BroadcastReceiver(Name = "com.winomail.android.NotificationActionReceiver", Exported = false)]
public sealed class NotificationActionReceiver : BroadcastReceiver
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(9);

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (!AndroidNotificationIntents.TryRead(intent, out var response, out var identity)) return;
        var pending = GoAsync();
        _ = HandleAsync(response, identity, pending);
    }

    private static async Task HandleAsync(Wino.Core.Domain.Models.Notifications.NotificationResponse response,
        AndroidNotificationIntents.NotificationIdentity identity, PendingResult? pending)
    {
        try
        {
            using var budget = new CancellationTokenSource(Budget);
            if (await AndroidBackgroundRuntime.EnsureAsync(budget.Token) is not { } services) return;
            services.GetRequiredService<AndroidNotificationBuilder>().Cancel(identity);
            Serilog.Log.Information("Notification action {Action} ({Kind}).", response.ActionIdentifier, response.Kind);
            await services.GetRequiredService<INotificationResponseService>().HandleAsync(response).WaitAsync(budget.Token);
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Notification action {Action} failed.", response.ActionIdentifier);
        }
        finally
        {
            pending?.Finish();
        }
    }
}
