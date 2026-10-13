using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Notification, reminder and Dock services. Owned by the notifications work (WS9).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterNotificationServices(IServiceCollection services)
    {
        // INotificationBuilder → AppleNotificationBuilder (notifications, Dock badge through MacDockBadgePresenter, Dock menu quick folders) is
        // registered with the platform services in Composition; its dependencies resolve from the container.
        // ICalendarReminderServer and INotificationResponseService are registered by Wino.Services; the
        // response service shows UI through this navigator (over IApplicationShellHost, see the Activation partial).
        // AppleNotificationResponseHandler (Wino.Platform.Apple) is created by AppDelegate before launch finishes (it must be the
        // notification center delegate before any service exists), so it is not a container service.
        services.AddSingleton<INotificationResponseNavigator, NotificationResponseNavigator>();
    }
}
