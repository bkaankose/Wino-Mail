using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Notification, reminder and Dock services. Owned by the notifications work (WS9).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterNotificationServices(IServiceCollection services)
    {
        // INotificationBuilder → MacNotificationBuilder (notifications, Dock badge and Dock menu model) is
        // registered with the platform services in Composition; its dependencies resolve from the container.
        // ICalendarReminderServer is registered by Wino.Services and started in AppDelegate.Notifications.
        // MacNotificationResponseHandler is created by AppDelegate before launch finishes (it must be the
        // notification center delegate before any service exists), so it is not a container service.
    }
}
