using AppKit;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Apple.PushRelay;
#if DEBUG
using Wino.Mail.MacOS.Infrastructure;
#endif

namespace Wino.Mail.MacOS;

/// <summary>
/// Live updates through WinoRelay on the Mac come over the socket only (<c>RelaySocketCoordinator</c>) while the app
/// runs; the Mac registers no push token, so a quit app receives nothing. This part refreshes the relay registration
/// when the app becomes active and hosts the Debug bridge commands.
/// </summary>
public sealed partial class AppDelegate
{
    partial void PushRelayServicesReady()
    {
#if DEBUG
        if (_services is { } services) RegisterPushRelayDebugCommands(services);
#endif
    }

    public override void DidBecomeActive(NSNotification notification)
    {
        // The coordinator decides whether a stalled relay deserves a retry now, and throttles repeated activations.
        if (_runtimeStarted) _services?.GetService<ISyncCoordinator>()?.RequestRelayRetry(RelayRetryTrigger.Foreground);
    }

#if DEBUG
    /// <summary>"relay status|sync|test|on|off" drives live notifications without the settings page.</summary>
    private static void RegisterPushRelayDebugCommands(IServiceProvider services)
    {
        var relay = services.GetRequiredService<IPushRelayRegistrationService>();
        MacDebugBridge.Register("relay", async args =>
        {
            switch (args.Length > 0 ? args[0].ToLowerInvariant() : "status")
            {
                case "delivered":
                    // What Notification Center currently holds for the app.
                    var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    UserNotifications.UNUserNotificationCenter.Current.GetDeliveredNotifications(list =>
                        delivered.TrySetResult($"{list.Length} delivered: " + string.Join(" | ", list.Select(n =>
                            $"{((DateTime)n.Date).ToLocalTime():HH:mm:ss} {n.Request.Content.Title}"))));
                    return await delivered.Task;
                case "device":
                    // What the relay knows about this device's push token.
                    if (await services.GetRequiredService<IPushRelayDeviceIdentityStore>().LoadAsync() is not { } identity) return "no identity";
                    var device = await services.GetRequiredService<IPushRelayClient>().GetDeviceAsync(identity.DeviceId);
                    return $"platform={device.Platform} tokenRegistered={device.PushTokenRegistered} invalidatedAt={device.PushTokenInvalidatedAt:O} " +
                        $"reason={device.PushTokenInvalidReason ?? "none"} app={device.AppVersion}/{device.AppBuild}";
                case "sync": await relay.SyncNowAsync(PushRelaySyncReason.UserRequested); break;
                case "test": return "test " + await relay.SendPushTestAsync();
                case "on": await relay.SetUserOptInAsync(true); break;
                case "off": await relay.SetUserOptInAsync(false); break;
                case "socket":
                    // The raw socket coordinator next to the sync coordinator's view (what the shell's red dot and the runtime read).
                    if (services.GetService<IRelaySocketCoordinator>() is not { } health) return "no socket coordinator";
                    var coordinator = services.GetRequiredService<ISyncCoordinator>().Snapshot;
                    var coverage = string.Join(", ", coordinator.Coverage.Values.Select(c => $"{c.AccountId.ToString()[..8]}={c.Kind}/{c.Gap}"));
                    return $"method={services.GetRequiredService<IPreferencesService>().SyncMethod} socket connected={health.IsSocketConnected} outage={health.LastOutage} reconnecting={health.IsReconnecting} " +
                        $"mode={coordinator.Mode} fallback={coordinator.Mode == EffectiveSyncMode.RelayFallback} cooldown={coordinator.IsFlapCooldownActive} changed={coordinator.ChangedUtc:HH:mm:ss} coverage=[{coverage}]";
            }

            var s = relay.Snapshot;
            var mailboxes = string.Join(", ", s.Mailboxes.Select(m => $"{m.AccountId.ToString()[..8]}={m.Status ?? "none"}{(m.LastErrorCode is { } e ? "/" + e : "")}"));
            return $"{s.Availability} optin={s.IsUserOptedIn} device={s.DeviceId?.ToString()[..8] ?? "none"} token={s.HasPushToken} " +
                $"checkin={s.LastCheckInUtc:HH:mm:ss} error={s.LastErrorCode ?? "none"} test={s.LastTestPushUtc:HH:mm:ss} mailboxes=[{mailboxes}]";
        });
    }
#endif
}
