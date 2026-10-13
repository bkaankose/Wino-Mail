using System.Runtime.Versioning;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UserNotifications;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;

namespace Wino.Platform.Apple.Services;

/// <summary>
/// The notification center delegate shared by the Mac and iOS apps. It reduces a native response to a <see cref="NotificationResponse"/>
/// and hands it to the shared <see cref="INotificationResponseService"/>, which holds the rules (the Windows
/// AppNotificationHandler and App.xaml.cs toast handlers). It is the delegate from launch, so a tap that
/// launched the app is kept until <see cref="Attach"/> supplies the services after the runtime starts.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios17.0")]
public sealed class AppleNotificationResponseHandler : UNUserNotificationCenterDelegate
{
    private readonly List<NotificationResponse> _pending = [];
    private readonly object _lock = new();
    private IServiceProvider? _services;

    public void Attach(IServiceProvider services)
    {
        NotificationResponse[] pending;
        lock (_lock)
        {
            _services = services;
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var response in pending) Observe(HandleAsync(response));
    }

    public void Detach()
    {
        lock (_lock) _services = null;
    }

    // Wino shows its notifications while it is in front too, like Windows toasts.
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
        => completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Sound);

    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
    {
        try
        {
            var copy = Translate(response);
            bool handleNow;
            lock (_lock)
            {
                handleNow = _services is not null;
                if (!handleNow) _pending.Add(copy);
            }
            if (handleNow) Observe(HandleAsync(copy));
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Could not read a notification response.");
        }
        finally
        {
            completionHandler();
        }
    }

    /// <summary>Copies what the shared rules need; the native objects are not kept across the queue.</summary>
    private static NotificationResponse Translate(UNNotificationResponse response)
    {
        var kind = response.IsDismissAction ? NotificationResponseKind.Dismiss
            : response.IsDefaultAction ? NotificationResponseKind.Default
            : NotificationResponseKind.Action;
        var content = response.Notification.Request.Content;
        return new NotificationResponse(
            kind,
            response.ActionIdentifier ?? string.Empty,
            content.CategoryIdentifier,
            (response as UNTextInputNotificationResponse)?.UserText,
            ReadValues(content.UserInfo));
    }

    private static Dictionary<string, string> ReadValues(NSDictionary? userInfo)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userInfo is null) return values;
        foreach (var key in userInfo.Keys)
        {
            if (userInfo[key] is NSString value) values[key.ToString()] = value.ToString();
        }
        return values;
    }

    private INotificationResponseService Service
        => (_services ?? throw new InvalidOperationException("Notification responses arrived before the services started."))
            .GetRequiredService<INotificationResponseService>();

    private static void Observe(Task task)
        => _ = task.ContinueWith(t => Serilog.Log.Error(t.Exception, "Notification response handling failed."), TaskContinuationOptions.OnlyOnFaulted);

    public async Task HandleAsync(NotificationResponse response) => await Service.HandleAsync(response);

    // Other surfaces (the Mac companion window and status item) open mail and calendar items the way a notification tap does.

    public async Task NavigateMailAsync(Guid mailUniqueId) => await Service.OpenMailAsync(mailUniqueId);

    public async Task NavigateCalendarItemAsync(Guid calendarItemId) => await Service.OpenCalendarItemAsync(calendarItemId);

    public async Task JoinOnlineAsync(Guid calendarItemId) => await Service.JoinOnlineAsync(calendarItemId);
}
