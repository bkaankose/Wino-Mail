using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Launch;

namespace Wino.Mail.WinUI.Activation;

internal enum AppActivationPath
{
    StartupTask,
    WelcomeWithoutAccounts,
    StandardLaunch,
    ShareTarget,
    MailToProtocol,
    CalendarEntryBootstrap,
    AppNotification,
    ToastLaunch,
    ModeActivation
}

internal enum NotificationActivationPath
{
    StoreUpdate,
    Dismiss,
    CalendarNavigation,
    CalendarSnooze,
    CalendarJoinOnline,
    MailNavigation,
    MailCompose,
    MailBackgroundAction,
    Unknown
}

internal readonly record struct NotificationActivationRoute(
    NotificationActivationPath Path,
    bool RequiresForegroundWindow,
    Func<Task>? ExecuteAsync);

internal readonly record struct LaunchActivationRoute(
    AppActivationPath Path,
    Microsoft.UI.Xaml.LaunchActivatedEventArgs LaunchArgs,
    AppActivationArguments ActivationArgs,
    AppNotificationActivatedEventArgs? AppNotificationArgs = null,
    NotificationActivationRoute NotificationRoute = default,
    NotificationArguments? ToastArguments = null,
    MailToUri? MailToUri = null,
    PendingBootstrapActivation? PendingBootstrapActivation = null)
{
    public bool RequiresAppHostInfrastructure => Path switch
    {
        AppActivationPath.AppNotification => NotificationRoute.RequiresForegroundWindow,
        _ => true
    };
}

internal readonly record struct RedirectedActivationRoute(
    AppActivationPath Path,
    AppActivationArguments ActivationArgs,
    WinoApplicationMode ActivationMode,
    bool ShouldActivateWindow,
    string? ShellActivationArguments = null,
    string? ShellActivationTileId = null,
    NotificationArguments? ToastArguments = null,
    IDictionary<string, string>? UserInput = null,
    MailToUri? MailToUri = null,
    AppNotificationActivatedEventArgs? AppNotificationArgs = null);

internal static class ActivationPathNames
{
    public static string GetDisplayName(AppActivationPath path)
        => path switch
        {
            AppActivationPath.StartupTask => "startup task",
            AppActivationPath.WelcomeWithoutAccounts => "welcome window without accounts",
            AppActivationPath.StandardLaunch => "standard launch",
            AppActivationPath.ShareTarget => "share target",
            AppActivationPath.MailToProtocol => "mailto protocol",
            AppActivationPath.CalendarEntryBootstrap => "calendar entry bootstrap",
            AppActivationPath.AppNotification => "app notification",
            AppActivationPath.ToastLaunch => "toast launch",
            AppActivationPath.ModeActivation => "mode activation",
            _ => path.ToString()
        };

    public static string GetDisplayName(NotificationActivationPath path)
        => path switch
        {
            NotificationActivationPath.StoreUpdate => "store update notification",
            NotificationActivationPath.Dismiss => "notification dismiss",
            NotificationActivationPath.CalendarNavigation => "calendar notification navigation",
            NotificationActivationPath.CalendarSnooze => "calendar notification snooze",
            NotificationActivationPath.CalendarJoinOnline => "calendar notification join online",
            NotificationActivationPath.MailNavigation => "mail notification navigation",
            NotificationActivationPath.MailCompose => "mail notification compose",
            NotificationActivationPath.MailBackgroundAction => "mail notification background action",
            NotificationActivationPath.Unknown => "unknown notification",
            _ => path.ToString()
        };
}
