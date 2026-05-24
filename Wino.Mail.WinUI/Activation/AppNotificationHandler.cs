using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.WinUI;

namespace Wino.Mail.WinUI.Activation;

internal sealed class AppNotificationHandler
{
    private readonly IAppNotificationHandlerHost _host;

    public AppNotificationHandler(IAppNotificationHandlerHost host)
    {
        _host = host;
    }

    public bool TryResolveActivationRoute(AppNotificationActivatedEventArgs notificationArgs,
                                          out NotificationActivationRoute route)
    {
        route = default;

        if (!ToastActivationResolver.TryParse(notificationArgs.Argument, out var toastArguments))
            return false;

        return TryCreateActivationRoute(toastArguments, notificationArgs.UserInput, out route);
    }

    public async Task HandleStartupActivationAsync(LaunchActivationRoute route)
    {
        var toastArgs = route.AppNotificationArgs
                        ?? throw new ArgumentException("Startup app-notification activation requires notification arguments.", nameof(route));

        _host.LogActivation($"Processing app-notification activation from startup. Arguments: {toastArgs.Argument}");

        if (route.NotificationRoute.ExecuteAsync == null)
        {
            await HandleActivationAsync(toastArgs.Argument, toastArgs.UserInput);

            if (!_host.IsAppRunning())
            {
                _host.LogActivation("Startup app-notification activation completed without a window. Exiting transient process.");
                _host.ExitApplication();
            }

            return;
        }

        LogNotificationRoute(route.NotificationRoute);
        await route.NotificationRoute.ExecuteAsync.Invoke();

        if (!route.NotificationRoute.RequiresForegroundWindow &&
            !_host.IsAppRunning())
        {
            _host.LogActivation("Background startup app-notification activation completed. Exiting without creating app host.");
            _host.ExitApplication();
        }
    }

    public async Task HandleActivationAsync(NotificationArguments toastArguments, IDictionary<string, string>? userInput = null)
    {
        if (!TryCreateActivationRoute(toastArguments, userInput, out var route))
        {
            _host.LogActivation("App notification activation did not match any known handler.");
            return;
        }

        LogNotificationRoute(route);
        await route.ExecuteAsync!.Invoke();
    }

    public Task HandleActivationAsync(string toastArgument, IDictionary<string, string>? userInput = null)
    {
        if (!ToastActivationResolver.TryParse(toastArgument, out var toastArguments))
        {
            _host.LogActivation($"Ignoring non-toast launch argument: {toastArgument}");
            return Task.CompletedTask;
        }

        return HandleActivationAsync(toastArguments, userInput);
    }

    private bool TryCreateActivationRoute(NotificationArguments toastArguments,
                                          IDictionary<string, string>? userInput,
                                          out NotificationActivationRoute route)
    {
        if (toastArguments.TryGetValue(Constants.ToastStoreUpdateActionKey, out string storeUpdateAction) &&
            storeUpdateAction == Constants.ToastStoreUpdateActionInstall)
        {
            route = new NotificationActivationRoute(NotificationActivationPath.StoreUpdate, true, _host.HandleStoreUpdateToastAsync);
            return true;
        }

        if (toastArguments.TryGetValue(Constants.ToastDismissActionKey, out string _))
        {
            route = new NotificationActivationRoute(NotificationActivationPath.Dismiss, false, _host.HandleNotificationDismissAsync);
            return true;
        }

        if (toastArguments.TryGetValue(Constants.ToastCalendarActionKey, out string calendarAction) &&
            toastArguments.TryGetValue(Constants.ToastCalendarItemIdKey, out string calendarItemIdString) &&
            Guid.TryParse(calendarItemIdString, out Guid calendarItemId))
        {
            route = calendarAction switch
            {
                Constants.ToastCalendarNavigateAction => new NotificationActivationRoute(NotificationActivationPath.CalendarNavigation, true, () => _host.HandleCalendarToastNavigationAsync(calendarItemId)),
                Constants.ToastCalendarSnoozeAction => new NotificationActivationRoute(NotificationActivationPath.CalendarSnooze, false, () => _host.HandleCalendarToastSnoozeAsync(userInput, calendarItemId)),
                Constants.ToastCalendarJoinOnlineAction => new NotificationActivationRoute(NotificationActivationPath.CalendarJoinOnline, false, () => _host.HandleCalendarToastJoinOnlineAsync(calendarItemId)),
                _ => default
            };

            return route.ExecuteAsync != null;
        }

        if (toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation action) &&
            Guid.TryParse(toastArguments[Constants.ToastMailUniqueIdKey], out Guid mailItemUniqueId))
        {
            if (action == MailOperation.Navigate)
            {
                route = new NotificationActivationRoute(NotificationActivationPath.MailNavigation, true, () => _host.HandleMailToastNavigationAsync(mailItemUniqueId));
                return true;
            }

            if (IsComposeToastAction(action))
            {
                route = new NotificationActivationRoute(NotificationActivationPath.MailCompose, true, () => _host.HandleMailToastComposeActionAsync(action, mailItemUniqueId));
                return true;
            }

            route = new NotificationActivationRoute(NotificationActivationPath.MailBackgroundAction, false, () => _host.HandleMailToastBackgroundActionAsync(action, mailItemUniqueId));
            return true;
        }

        route = default;
        return false;
    }

    private void LogNotificationRoute(NotificationActivationRoute route)
        => _host.LogActivation(route.RequiresForegroundWindow
            ? $"Handling foreground notification path: {ActivationPathNames.GetDisplayName(route.Path)}."
            : $"Handling background notification path: {ActivationPathNames.GetDisplayName(route.Path)}.");

    private static bool IsComposeToastAction(MailOperation action)
        => action is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;
}
