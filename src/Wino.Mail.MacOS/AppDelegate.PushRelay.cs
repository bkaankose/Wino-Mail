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
/// Live notifications through WinoRelay: registers for APNs, forwards the device token to
/// <see cref="ApplePushTokenProvider"/>, hands data-only pushes to the shared notification handler and refreshes the
/// relay registration when the app becomes active. Registration with the relay itself is opt-in (Settings ›
/// Notifications) and runs in the shared relay service.
/// </summary>
public sealed partial class AppDelegate
{
    private static readonly TimeSpan RemoteNotificationBudget = TimeSpan.FromSeconds(25);

    partial void PushRelayServicesReady()
    {
        // ServicesReady runs on the startup task; AppKit wants the registration on the main thread.
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            try { NSApplication.SharedApplication.RegisterForRemoteNotifications(); }
            catch (Exception error) { Serilog.Log.Warning(error, "Could not register for remote notifications."); }
        });
#if DEBUG
        if (_services is { } services) RegisterPushRelayDebugCommands(services);
#endif
    }

    public override void RegisteredForRemoteNotifications(NSApplication application, NSData deviceToken)
    {
        Serilog.Log.Information("Registered for remote notifications ({Length} byte token).", deviceToken.Length);
        _services?.GetService<ApplePushTokenProvider>()?.SetToken(deviceToken);
    }

    public override void FailedToRegisterForRemoteNotifications(NSApplication application, NSError error)
    {
        // Expected for ad hoc builds and profiles without the Push Notifications capability.
        Serilog.Log.Information("Remote notification registration failed: {Error}", error.LocalizedDescription);
        _services?.GetService<ApplePushTokenProvider>()?.SetFailure(error.LocalizedDescription);
    }

    public override void ReceivedRemoteNotification(NSApplication application, NSDictionary userInfo)
    {
        var payload = ApplePushPayload.ToPayload(userInfo);
        // Kind and reason only: the payload also carries account and mailbox ids.
        Serilog.Log.Information("Relay push received: {Kind} ({Reason}).",
            payload.GetValueOrDefault("t", "?"), payload.GetValueOrDefault("r", "-"));
        if (!_runtimeStarted || _services?.GetService<IPushRelayNotificationHandler>() is not { } handler) return;
        _ = HandleRemoteNotificationAsync(handler, payload);
    }

    public override void DidBecomeActive(NSNotification notification)
    {
        if (_runtimeStarted) _services?.GetService<IPushRelayRegistrationService>()?.RequestSync(PushRelaySyncReason.Foreground);
    }

    private static async Task HandleRemoteNotificationAsync(IPushRelayNotificationHandler handler, Dictionary<string, string> payload)
    {
        try
        {
            using var budget = new CancellationTokenSource(RemoteNotificationBudget);
            var outcome = await handler.HandleAsync(payload, budget.Token);
            Serilog.Log.Debug("Relay push handled: {Outcome}", outcome);
        }
        catch (OperationCanceledException) { Serilog.Log.Warning("Relay push handling did not finish within {Seconds} s.", RemoteNotificationBudget.TotalSeconds); }
        catch (Exception error) { Serilog.Log.Warning(error, "Relay push handling failed."); }
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
                    // The desktop socket coordinator: what the shell's red dot and the runtime's fallback loops read.
                    if (services.GetService<IRelaySocketCoordinator>() is not { } health) return "no socket coordinator";
                    return $"method={services.GetRequiredService<IPreferencesService>().SyncMethod} connected={health.IsSocketConnected} fallback={health.IsFallbackActive} changed={health.LastChangeUtc:HH:mm:ss}";
            }

            var s = relay.Snapshot;
            var mailboxes = string.Join(", ", s.Mailboxes.Select(m => $"{m.AccountId.ToString()[..8]}={m.Status ?? "none"}{(m.LastErrorCode is { } e ? "/" + e : "")}"));
            return $"{s.Availability} optin={s.IsUserOptedIn} device={s.DeviceId?.ToString()[..8] ?? "none"} token={s.HasPushToken} " +
                $"checkin={s.LastCheckInUtc:HH:mm:ss} error={s.LastErrorCode ?? "none"} test={s.LastTestPushUtc:HH:mm:ss} mailboxes=[{mailboxes}]";
        });
    }
#endif
}
