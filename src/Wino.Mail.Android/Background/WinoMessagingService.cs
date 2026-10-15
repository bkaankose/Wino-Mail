using Android.App;
using Firebase.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Android.PushRelay;

namespace Wino.Mail.Android.Background;

/// <summary>
/// FCM for WinoRelay (compile-only for v1, owner decision: written and registered, not tested or relied on). Data
/// messages go to the shared <see cref="IPushRelayNotificationHandler"/> (the ApplePushPayload path). The token is read
/// at every start (<see cref="AndroidFcmTokenProvider.RequestToken"/>); the binding marks <c>onNewToken</c> obsolete, so
/// a token that rotates while the app runs reaches the relay at the next start. Firebase only runs in builds with
/// google-services.json, so without it this service never receives anything.
/// </summary>
[Service(Name = "com.winomail.android.WinoMessagingService", Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class WinoMessagingService : FirebaseMessagingService
{
    /// <summary>FCM gives a data message about 20 seconds.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(18);

    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);
        var payload = AndroidPushPayload.ToPayload(message);
        if (payload.Count == 0) return;
        try
        {
            // FCM calls this on a worker thread and ends the wake-up when it returns.
            Task.Run(async () =>
            {
                using var budget = new CancellationTokenSource(Budget);
                if (await AndroidBackgroundRuntime.EnsureAsync(budget.Token) is not { } services) return;
                var outcome = await services.GetRequiredService<IPushRelayNotificationHandler>().HandleAsync(payload, budget.Token);
                Serilog.Log.Information("Relay push handled: {Outcome}.", outcome);
            }).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Relay push handling failed.");
        }
    }
}
