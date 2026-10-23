using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using MailCal.Core.Activation;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Launch;
using MailCal.NotificationHost.Contracts;

namespace MailCal.Mail.WinUI.Activation;

internal enum AppActivationPath
{
    StartupTask,
    SessionStart,
    WelcomeWithoutAccounts,
    StandardLaunch,
    ShareTarget,
    MailToProtocol,
    SecondaryEntryBootstrap,
    FileImport,
    AppNotification,
    ForwardedAppNotification,
    ToastLaunch,
    ModeActivation
}

internal enum NotificationActivationPath
{
    Dismiss,
    CalendarNavigation,
    CalendarSnooze,
    CalendarJoinOnline,
    MailNavigation,
    MailCompose,
    MailBackgroundAction,
    ModeNavigation,
    Unknown
}

internal readonly record struct NotificationActivationRoute(
    NotificationActivationPath Path,
    bool RequiresForegroundWindow,
    Func<Task>? ExecuteAsync);

internal readonly record struct LaunchActivationRoute(
    AppActivationPath Path,
    Microsoft.UI.Xaml.LaunchActivatedEventArgs LaunchArgs,
    AppActivationArguments? ActivationArgs,
    AppNotificationActivatedEventArgs? AppNotificationArgs = null,
    NotificationActivationRoute NotificationRoute = default,
    NotificationArguments? ToastArguments = null,
    MailToUri? MailToUri = null,
    PendingBootstrapActivation? PendingBootstrapActivation = null,
    MailCalApplicationMode ActivationMode = MailCalApplicationMode.Mail,
    IReadOnlyList<string>? FilePaths = null,
    NotificationHostActivation? ForwardedNotificationActivation = null)
{
    public bool RequiresAppHostInfrastructure => Path switch
    {
        AppActivationPath.AppNotification => NotificationRoute.RequiresForegroundWindow,
        AppActivationPath.ForwardedAppNotification => NotificationRoute.RequiresForegroundWindow,
        _ => true
    };
}

internal readonly record struct RedirectedActivationRoute(
    AppActivationPath Path,
    AppActivationArguments ActivationArgs,
    MailCalApplicationMode ActivationMode,
    bool ShouldActivateWindow,
    string? ShellActivationArguments = null,
    string? ShellActivationTileId = null,
    NotificationArguments? ToastArguments = null,
    IDictionary<string, string>? UserInput = null,
    MailToUri? MailToUri = null,
    AppNotificationActivatedEventArgs? AppNotificationArgs = null,
    IReadOnlyList<string>? FilePaths = null,
    NotificationHostActivation? ForwardedNotificationActivation = null,
    NotificationActivationRoute NotificationRoute = default);

internal static class ActivationPathNames
{
    public static string GetDisplayName(AppActivationPath path)
        => path switch
        {
            AppActivationPath.StartupTask => "startup task",
            AppActivationPath.SessionStart => "session-start background task",
            AppActivationPath.WelcomeWithoutAccounts => "welcome window without accounts",
            AppActivationPath.StandardLaunch => "standard launch",
            AppActivationPath.ShareTarget => "share target",
            AppActivationPath.MailToProtocol => "mailto protocol",
            AppActivationPath.SecondaryEntryBootstrap => "secondary entry bootstrap",
            AppActivationPath.FileImport => "file import",
            AppActivationPath.AppNotification => "app notification",
            AppActivationPath.ForwardedAppNotification => "forwarded app notification",
            AppActivationPath.ToastLaunch => "toast launch",
            AppActivationPath.ModeActivation => "mode activation",
            _ => path.ToString()
        };

    public static string GetDisplayName(NotificationActivationPath path)
        => path switch
        {

            NotificationActivationPath.Dismiss => "notification dismiss",
            NotificationActivationPath.CalendarNavigation => "calendar notification navigation",
            NotificationActivationPath.CalendarSnooze => "calendar notification snooze",
            NotificationActivationPath.CalendarJoinOnline => "calendar notification join online",
            NotificationActivationPath.MailNavigation => "mail notification navigation",
            NotificationActivationPath.MailCompose => "mail notification compose",
            NotificationActivationPath.MailBackgroundAction => "mail notification background action",
            NotificationActivationPath.ModeNavigation => "notification mode navigation",
            NotificationActivationPath.Unknown => "unknown notification",
            _ => path.ToString()
        };
}
