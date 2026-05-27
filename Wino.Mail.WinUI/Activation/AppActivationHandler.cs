using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Launch;
using Wino.Mail.WinUI;

namespace Wino.Mail.WinUI.Activation;

internal sealed class AppActivationHandler
{
    private const string ToggleDefaultModeLaunchArgument = "--mode=toggle-default";

    private readonly IAppActivationHandlerHost _host;
    private readonly AppNotificationHandler _notificationHandler;

    public AppActivationHandler(IAppActivationHandlerHost host, AppNotificationHandler notificationHandler)
    {
        _host = host;
        _notificationHandler = notificationHandler;
    }

    public async Task HandleLaunchAsync(Microsoft.UI.Xaml.LaunchActivatedEventArgs launchArgs,
                                        AppActivationArguments activationArgs)
    {
        var route = ResolveLaunchActivationRoute(launchArgs, activationArgs);

        if (route.RequiresAppHostInfrastructure)
        {
            await _host.EnsureAppHostInfrastructureAsync();
        }

        await HandleLaunchActivationRouteAsync(route);
    }

    public async Task HandleRedirectedActivationAsync(AppActivationArguments args)
    {
        var route = ResolveRedirectedActivationRoute(args);

        await _host.EnsureActivationInfrastructureAsync();

        await HandleRedirectedActivationRouteAsync(route);
    }

    private LaunchActivationRoute ResolveLaunchActivationRoute(Microsoft.UI.Xaml.LaunchActivatedEventArgs launchArgs,
                                                               AppActivationArguments activationArgs)
    {
        if (TryCreateStartupNotificationActivationRoute(launchArgs, activationArgs, out var startupNotificationRoute))
            return startupNotificationRoute;

        if (activationArgs.Kind == ExtendedActivationKind.StartupTask)
            return new LaunchActivationRoute(AppActivationPath.StartupTask, launchArgs, activationArgs);

        if (!_host.HasConfiguredAccounts)
            return new LaunchActivationRoute(AppActivationPath.WelcomeWithoutAccounts, launchArgs, activationArgs);

        if (activationArgs.Kind == ExtendedActivationKind.ShareTarget &&
            _host.TryMarkInitialShareActivationHandled())
        {
            return new LaunchActivationRoute(AppActivationPath.ShareTarget, launchArgs, activationArgs);
        }

        if (TryCreateMailToUri(activationArgs, out var mailToUri))
        {
            return new LaunchActivationRoute(
                AppActivationPath.MailToProtocol,
                launchArgs,
                activationArgs,
                MailToUri: mailToUri);
        }

        if (Program.TryConsumePendingBootstrapActivation(out var pendingBootstrapActivation) &&
            CanHandlePendingBootstrapActivation(pendingBootstrapActivation))
        {
            return new LaunchActivationRoute(
                AppActivationPath.CalendarEntryBootstrap,
                launchArgs,
                activationArgs,
                PendingBootstrapActivation: pendingBootstrapActivation);
        }

        if (ToastActivationResolver.TryParse(launchArgs.Arguments, out var launchToastArguments) &&
            _host.TryMarkInitialNotificationActivationHandled())
        {
            return new LaunchActivationRoute(
                AppActivationPath.ToastLaunch,
                launchArgs,
                activationArgs,
                ToastArguments: launchToastArguments);
        }

        return new LaunchActivationRoute(AppActivationPath.StandardLaunch, launchArgs, activationArgs);
    }

    private bool TryCreateStartupNotificationActivationRoute(Microsoft.UI.Xaml.LaunchActivatedEventArgs launchArgs,
                                                             AppActivationArguments activationArgs,
                                                             out LaunchActivationRoute route)
    {
        route = default;

        if (activationArgs.Kind != ExtendedActivationKind.AppNotification ||
            activationArgs.Data is not AppNotificationActivatedEventArgs notificationArgs ||
            !_host.TryMarkInitialNotificationActivationHandled())
        {
            return false;
        }

        _notificationHandler.TryResolveActivationRoute(notificationArgs, out var notificationRoute);

        route = new LaunchActivationRoute(
            AppActivationPath.AppNotification,
            launchArgs,
            activationArgs,
            AppNotificationArgs: notificationArgs,
            NotificationRoute: notificationRoute);

        return true;
    }

    private async Task HandleLaunchActivationRouteAsync(LaunchActivationRoute route)
    {
        _host.LogActivation($"Handling launch activation path: {ActivationPathNames.GetDisplayName(route.Path)}.");

        switch (route.Path)
        {
            case AppActivationPath.AppNotification:
                await _notificationHandler.HandleStartupActivationAsync(route);
                break;
            case AppActivationPath.StartupTask:
                _host.CompleteStartupTaskLaunch(_host.HasConfiguredAccounts);
                break;
            case AppActivationPath.WelcomeWithoutAccounts:
                await _host.LaunchWelcomeWindowAsync();
                break;
            case AppActivationPath.ShareTarget:
                _host.LogActivation("Processing share target activation from OnLaunched.");
                if (!await _host.HandleShareTargetActivationAsync(route.ActivationArgs, activateWindow: true))
                {
                    await _host.CompleteStandardLaunchAsync(route.LaunchArgs, _host.HasConfiguredAccounts);
                }
                break;
            case AppActivationPath.MailToProtocol:
                _host.LogActivation("Processing mailto protocol activation from OnLaunched.");
                if (route.MailToUri == null ||
                    !await _host.HandleMailToProtocolActivationAsync(route.MailToUri, activateWindow: true))
                {
                    await _host.CompleteStandardLaunchAsync(route.LaunchArgs, _host.HasConfiguredAccounts);
                }
                break;
            case AppActivationPath.CalendarEntryBootstrap:
                if (route.PendingBootstrapActivation != null)
                {
                    _host.LogActivation($"Processing pending bootstrap activation. Kind: {route.PendingBootstrapActivation.Kind}, Mode: {route.PendingBootstrapActivation.Mode}");
                    if (!await _host.HandlePendingBootstrapActivationAsync(route.PendingBootstrapActivation))
                    {
                        await _host.CompleteStandardLaunchAsync(route.LaunchArgs, _host.HasConfiguredAccounts);
                    }
                }
                break;
            case AppActivationPath.ToastLaunch:
                _host.LogActivation($"Processing toast launch activation from OnLaunched. Arguments: {route.LaunchArgs.Arguments}");
                await _notificationHandler.HandleActivationAsync(route.ToastArguments!);
                break;
            case AppActivationPath.StandardLaunch:
                await _host.CompleteStandardLaunchAsync(route.LaunchArgs, _host.HasConfiguredAccounts);
                break;
        }
    }

    private RedirectedActivationRoute ResolveRedirectedActivationRoute(AppActivationArguments args)
    {
        var activationMode = _host.DefaultApplicationMode;
        var shouldActivateWindow = args.Kind != ExtendedActivationKind.StartupTask;

        if (args.Kind == ExtendedActivationKind.AppNotification &&
            args.Data is AppNotificationActivatedEventArgs notificationArgs)
        {
            ToastActivationResolver.TryParse(notificationArgs.Argument, out var notificationToastArguments);

            return new RedirectedActivationRoute(
                AppActivationPath.AppNotification,
                args,
                activationMode,
                shouldActivateWindow,
                ToastArguments: notificationToastArguments,
                UserInput: CopyUserInput(notificationArgs.UserInput));
        }

        if (args.Kind == ExtendedActivationKind.ShareTarget)
        {
            return new RedirectedActivationRoute(
                AppActivationPath.ShareTarget,
                args,
                WinoApplicationMode.Mail,
                shouldActivateWindow);
        }

        if (TryCreateMailToUri(args, out var mailToUri))
        {
            return new RedirectedActivationRoute(
                AppActivationPath.MailToProtocol,
                args,
                WinoApplicationMode.Mail,
                shouldActivateWindow,
                MailToUri: mailToUri);
        }

        if (args.Kind == ExtendedActivationKind.Launch &&
            args.Data is ILaunchActivatedEventArgs launchArgs)
        {
            if (ToastActivationResolver.TryParse(launchArgs.Arguments, out var launchToastArguments))
            {
                return new RedirectedActivationRoute(
                    AppActivationPath.ToastLaunch,
                    args,
                    activationMode,
                    ToastActivationResolver.ShouldBringToForeground(launchToastArguments),
                    ToastArguments: launchToastArguments);
            }

            var launchArguments = launchArgs.Arguments;
            var shellActivationTileId = launchArgs.TileId;

            if (RedirectedLaunchActivationOverride.TryConsume(args, out var redirectedLaunchActivation))
            {
                activationMode = redirectedLaunchActivation.Mode;
                launchArguments = redirectedLaunchActivation.LaunchArguments;
                shellActivationTileId = redirectedLaunchActivation.TileId ?? shellActivationTileId;
            }

            if (Program.TryConsumeRedirectedAlternateModeOverride())
            {
                launchArguments = AppendLaunchArgument(launchArguments, ToggleDefaultModeLaunchArgument);
            }

            activationMode = AppModeActivationResolver.Resolve(
                launchArguments,
                shellActivationTileId,
                null,
                activationMode);
            launchArguments = EnsureModeLaunchArgument(launchArguments, activationMode);

            return new RedirectedActivationRoute(
                AppActivationPath.StandardLaunch,
                args,
                activationMode,
                shouldActivateWindow,
                ShellActivationArguments: launchArguments,
                ShellActivationTileId: shellActivationTileId);
        }

        if (TryResolveActivationMode(args, activationMode, out var redirectedMode))
        {
            return new RedirectedActivationRoute(
                AppActivationPath.ModeActivation,
                args,
                redirectedMode,
                shouldActivateWindow,
                ShellActivationArguments: AppEntryConstants.GetModeLaunchArgument(redirectedMode));
        }

        return new RedirectedActivationRoute(
            args.Kind == ExtendedActivationKind.StartupTask ? AppActivationPath.StartupTask : AppActivationPath.StandardLaunch,
            args,
            activationMode,
            shouldActivateWindow);
    }

    private async Task HandleRedirectedActivationRouteAsync(RedirectedActivationRoute route)
    {
        _host.LogActivation($"Handling redirected activation path: {ActivationPathNames.GetDisplayName(route.Path)}.");

        if (route.Path == AppActivationPath.AppNotification)
        {
            if (route.ToastArguments != null)
            {
                _host.LogActivation("Processing redirected notification activation.");
                await _notificationHandler.HandleActivationAsync(route.ToastArguments, route.UserInput);
            }
            else
            {
                _host.LogActivation("Redirected app notification activation did not contain a toast payload.");
            }

            return;
        }

        if (route.Path == AppActivationPath.ShareTarget)
        {
            _host.LogActivation("Processing redirected share target activation.");
            await _host.HandleShareTargetActivationAsync(route.ActivationArgs, activateWindow: true);
            return;
        }

        if (route.Path == AppActivationPath.MailToProtocol)
        {
            _host.LogActivation("Processing redirected mailto protocol activation.");
            if (route.MailToUri != null)
            {
                await _host.HandleMailToProtocolActivationAsync(route.MailToUri, activateWindow: true);
            }
            return;
        }

        if (route.Path == AppActivationPath.ToastLaunch)
        {
            _host.LogActivation("Processing redirected toast launch activation.");
            await _notificationHandler.HandleActivationAsync(route.ToastArguments!);
            return;
        }

        await _host.ActivateRedirectedShellAsync(route);
    }

    private static bool CanHandlePendingBootstrapActivation(PendingBootstrapActivation pendingBootstrapActivation)
        => pendingBootstrapActivation.Mode == WinoApplicationMode.Calendar;

    private static bool TryCreateMailToUri(AppActivationArguments activationArgs, out MailToUri? mailToUri)
    {
        mailToUri = null;

        if (activationArgs.Kind != ExtendedActivationKind.Protocol ||
            activationArgs.Data is not IProtocolActivatedEventArgs { Uri: { } protocolUri } ||
            !string.Equals(protocolUri.Scheme, "mailto", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            mailToUri = new MailToUri(protocolUri.AbsoluteUri);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IDictionary<string, string>? CopyUserInput(IDictionary<string, string>? userInput)
        => userInput == null ? null : new Dictionary<string, string>(userInput);

    private static string AppendLaunchArgument(string? launchArguments, string launchArgument)
    {
        return string.IsNullOrWhiteSpace(launchArguments)
            ? launchArgument
            : $"{launchArguments} {launchArgument}";
    }

    private static string EnsureModeLaunchArgument(string? launchArguments, WinoApplicationMode mode)
    {
        return string.IsNullOrWhiteSpace(launchArguments)
            ? AppEntryConstants.GetModeLaunchArgument(mode)
            : launchArguments;
    }

    private static bool TryResolveActivationMode(AppActivationArguments activationArgs, WinoApplicationMode defaultMode, out WinoApplicationMode mode)
    {
        mode = defaultMode;

        if (activationArgs.Kind == ExtendedActivationKind.Protocol &&
            activationArgs.Data is IProtocolActivatedEventArgs protocolArgs)
        {
            var scheme = protocolArgs.Uri?.Scheme;

            if (string.Equals(scheme, "webcal", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scheme, "webcals", System.StringComparison.OrdinalIgnoreCase))
            {
                mode = WinoApplicationMode.Calendar;
                return true;
            }

            if (string.Equals(scheme, "mailto", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scheme, "google.pw.oauth2", System.StringComparison.OrdinalIgnoreCase))
            {
                mode = WinoApplicationMode.Mail;
                return true;
            }
        }

        if (activationArgs.Kind == ExtendedActivationKind.ShareTarget)
        {
            mode = WinoApplicationMode.Mail;
            return true;
        }

        if (activationArgs.Kind == ExtendedActivationKind.File &&
            activationArgs.Data is IFileActivatedEventArgs fileArgs)
        {
            var fileItem = fileArgs.Files?.FirstOrDefault();
            var extension = System.IO.Path.GetExtension(fileItem?.Name ?? string.Empty);

            if (string.Equals(extension, ".ics", System.StringComparison.OrdinalIgnoreCase))
            {
                mode = WinoApplicationMode.Calendar;
                return true;
            }

            if (string.Equals(extension, ".eml", System.StringComparison.OrdinalIgnoreCase))
            {
                mode = WinoApplicationMode.Mail;
                return true;
            }
        }

        if (activationArgs.Kind == ExtendedActivationKind.Launch &&
            activationArgs.Data is ILaunchActivatedEventArgs launchArgs)
        {
            mode = AppModeActivationResolver.Resolve(launchArgs.Arguments, launchArgs.TileId, null, defaultMode);
            return true;
        }

        return false;
    }
}
