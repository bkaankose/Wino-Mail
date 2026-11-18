using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Notifications;
using Wino.Mail.WinUI;
using Wino.NotificationHost.Contracts;

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

    public bool TryResolveActivationRoute(
        string argument,
        IDictionary<string, string>? userInput,
        out NotificationActivationRoute route)
    {
        route = default;

        return ToastActivationResolver.TryParse(argument, out var toastArguments) &&
               TryCreateActivationRoute(toastArguments, userInput, out route);
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

    public async Task HandleForwardedActivationAsync(
        NotificationHostActivation activation,
        NotificationActivationRoute route)
    {
        _host.LogActivation($"Processing forwarded app-notification activation for {activation.Application}.");

        if (route.ExecuteAsync == null)
        {
            await HandleActivationAsync(activation.Argument, new Dictionary<string, string>(activation.UserInput));
        }
        else
        {
            LogNotificationRoute(route);
            await route.ExecuteAsync.Invoke();
        }

        if (!route.RequiresForegroundWindow && !_host.IsAppRunning())
        {
            _host.LogActivation("Forwarded background notification activation completed. Exiting transient process.");
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
                Constants.ToastCalendarNavigateAction => CreateResponseRoute(NotificationActivationPath.CalendarNavigation, true,
                    CreateResponse(toastArguments, userInput, NotificationResponseKind.Default, string.Empty)),
                Constants.ToastCalendarSnoozeAction => CreateResponseRoute(NotificationActivationPath.CalendarSnooze, false,
                    CreateResponse(toastArguments, userInput, NotificationResponseKind.Action, NotificationActionIdentifiers.CalendarSnooze)),
                Constants.ToastCalendarJoinOnlineAction => CreateResponseRoute(NotificationActivationPath.CalendarJoinOnline, false,
                    CreateResponse(toastArguments, userInput, NotificationResponseKind.Action, NotificationActionIdentifiers.CalendarJoin)),
                _ => default
            };

            return route.ExecuteAsync != null;
        }

        if (toastArguments.TryGetValue(Constants.ToastActionKey, out MailOperation action) &&
            Guid.TryParse(toastArguments[Constants.ToastMailUniqueIdKey], out Guid mailItemUniqueId))
        {
            var mailValues = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Constants.ToastMailUniqueIdKey] = mailItemUniqueId.ToString()
            };

            if (action == MailOperation.Navigate)
            {
                route = CreateResponseRoute(NotificationActivationPath.MailNavigation, true,
                    new NotificationResponse(NotificationResponseKind.Default, string.Empty, null, null, mailValues));
                return true;
            }

            var mailResponse = new NotificationResponse(NotificationResponseKind.Action, NotificationActionIdentifiers.MailAction(action), null, null, mailValues);
            route = IsComposeToastAction(action)
                ? CreateResponseRoute(NotificationActivationPath.MailCompose, true, mailResponse)
                : CreateResponseRoute(NotificationActivationPath.MailBackgroundAction, false, mailResponse);
            return true;
        }

        if (toastArguments.TryGetValue(WidgetActions.ActionKey, out string widgetAction) &&
            WidgetActions.IsKnown(widgetAction))
        {
            var widgetValue = toastArguments.TryGetValue(WidgetActions.ValueKey, out string value) ? value : null;
            route = new NotificationActivationRoute(NotificationActivationPath.WidgetAction, true, () => _host.HandleWidgetActionAsync(widgetAction, widgetValue));
            return true;
        }

        if (ToastActivationResolver.TryResolveMode(toastArguments, out _))
        {
            toastArguments.TryGetValue(Constants.ToastModeKey, out string modeValue);
            route = CreateResponseRoute(NotificationActivationPath.ModeNavigation, true,
                new NotificationResponse(NotificationResponseKind.Default, string.Empty, null, null,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [Constants.ToastModeKey] = modeValue }));
            return true;
        }

        route = default;
        return false;
    }

    /// <summary>
    /// The head resolved which notification path this is; the shared service runs it. Mail and mode responses carry
    /// only the values of that path, so the service cannot pick another one from extra keys on the toast.
    /// </summary>
    private NotificationActivationRoute CreateResponseRoute(NotificationActivationPath path, bool requiresForegroundWindow, NotificationResponse response)
        => new(path, requiresForegroundWindow, () => _host.HandleNotificationResponseAsync(response));

    private static NotificationResponse CreateResponse(NotificationArguments toastArguments,
                                                       IDictionary<string, string>? userInput,
                                                       NotificationResponseKind kind,
                                                       string actionIdentifier)
    {
        var values = new Dictionary<string, string>(toastArguments.Values, StringComparer.Ordinal);

        if (userInput != null)
        {
            foreach (var (key, value) in userInput)
                values[key] = value;
        }

        return new NotificationResponse(kind, actionIdentifier, null, null, values);
    }

    private void LogNotificationRoute(NotificationActivationRoute route)
        => _host.LogActivation(route.RequiresForegroundWindow
            ? $"Handling foreground notification path: {ActivationPathNames.GetDisplayName(route.Path)}."
            : $"Handling background notification path: {ActivationPathNames.GetDisplayName(route.Path)}.");

    private static bool IsComposeToastAction(MailOperation action)
        => action is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;
}
