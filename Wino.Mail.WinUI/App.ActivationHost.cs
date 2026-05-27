using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Launch;
using Wino.Mail.WinUI.Activation;

namespace Wino.Mail.WinUI;

internal interface IAppActivationHandlerHost
{
    bool HasConfiguredAccounts { get; }
    WinoApplicationMode DefaultApplicationMode { get; }
    void LogActivation(string message);
    bool TryMarkInitialNotificationActivationHandled();
    bool TryMarkInitialShareActivationHandled();
    Task EnsureActivationInfrastructureAsync();
    Task EnsureAppHostInfrastructureAsync();
    void CompleteStartupTaskLaunch(bool hasAnyAccount);
    Task LaunchWelcomeWindowAsync();
    Task<bool> HandleShareTargetActivationAsync(AppActivationArguments activationArgs, bool activateWindow);
    Task<bool> HandleMailToProtocolActivationAsync(MailToUri mailToUri, bool activateWindow);
    Task<bool> HandlePendingBootstrapActivationAsync(PendingBootstrapActivation pendingBootstrapActivation);
    Task CompleteStandardLaunchAsync(Microsoft.UI.Xaml.LaunchActivatedEventArgs launchArgs, bool hasAnyAccount);
    Task ActivateRedirectedShellAsync(RedirectedActivationRoute route);
}

internal interface IAppNotificationHandlerHost
{
    bool IsAppRunning();
    void ExitApplication();
    void LogActivation(string message);
    Task HandleStoreUpdateToastAsync();
    Task HandleNotificationDismissAsync();
    Task HandleCalendarToastNavigationAsync(Guid calendarItemId);
    Task HandleCalendarToastSnoozeAsync(IDictionary<string, string>? userInput, Guid calendarItemId);
    Task HandleCalendarToastJoinOnlineAsync(Guid calendarItemId);
    Task HandleMailToastNavigationAsync(Guid mailItemUniqueId);
    Task HandleMailToastComposeActionAsync(MailOperation action, Guid mailItemUniqueId);
    Task HandleMailToastBackgroundActionAsync(MailOperation action, Guid mailItemUniqueId);
}

public partial class App : IAppActivationHandlerHost, IAppNotificationHandlerHost
{
    bool IAppActivationHandlerHost.HasConfiguredAccounts => _hasConfiguredAccounts;
    WinoApplicationMode IAppActivationHandlerHost.DefaultApplicationMode => _preferencesService?.DefaultApplicationMode ?? WinoApplicationMode.Mail;
    void IAppActivationHandlerHost.LogActivation(string message) => LogActivation(message);
    bool IAppActivationHandlerHost.TryMarkInitialNotificationActivationHandled() => TryMarkInitialNotificationActivationHandled();
    bool IAppActivationHandlerHost.TryMarkInitialShareActivationHandled() => TryMarkInitialShareActivationHandled();
    Task IAppActivationHandlerHost.EnsureActivationInfrastructureAsync() => EnsureActivationInfrastructureAsync();
    Task IAppActivationHandlerHost.EnsureAppHostInfrastructureAsync() => EnsureAppHostInfrastructureAsync();
    void IAppActivationHandlerHost.CompleteStartupTaskLaunch(bool hasAnyAccount) => CompleteStartupTaskLaunch(hasAnyAccount);
    Task IAppActivationHandlerHost.LaunchWelcomeWindowAsync() => LaunchWelcomeWindowAsync();
    Task<bool> IAppActivationHandlerHost.HandleShareTargetActivationAsync(AppActivationArguments activationArgs, bool activateWindow)
        => HandleShareTargetActivationAsync(activationArgs, activateWindow);
    Task<bool> IAppActivationHandlerHost.HandleMailToProtocolActivationAsync(MailToUri mailToUri, bool activateWindow)
        => HandleMailToProtocolActivationAsync(mailToUri, activateWindow);
    Task<bool> IAppActivationHandlerHost.HandlePendingBootstrapActivationAsync(PendingBootstrapActivation pendingBootstrapActivation)
        => HandlePendingBootstrapActivationAsync(pendingBootstrapActivation);
    Task IAppActivationHandlerHost.CompleteStandardLaunchAsync(Microsoft.UI.Xaml.LaunchActivatedEventArgs launchArgs, bool hasAnyAccount)
        => CompleteStandardLaunchAsync(launchArgs, hasAnyAccount);
    Task IAppActivationHandlerHost.ActivateRedirectedShellAsync(RedirectedActivationRoute route)
        => ActivateRedirectedShellAsync(route);

    bool IAppNotificationHandlerHost.IsAppRunning() => IsAppRunning();
    void IAppNotificationHandlerHost.ExitApplication() => ExitApplication();
    void IAppNotificationHandlerHost.LogActivation(string message) => LogActivation(message);
    Task IAppNotificationHandlerHost.HandleStoreUpdateToastAsync() => HandleStoreUpdateToastAsync();
    Task IAppNotificationHandlerHost.HandleNotificationDismissAsync()
    {
        LogActivation("Handling notification dismiss action.");
        return Task.CompletedTask;
    }
    Task IAppNotificationHandlerHost.HandleCalendarToastNavigationAsync(Guid calendarItemId)
        => HandleCalendarToastNavigationAsync(calendarItemId);
    Task IAppNotificationHandlerHost.HandleCalendarToastSnoozeAsync(IDictionary<string, string>? userInput, Guid calendarItemId)
        => HandleCalendarToastSnoozeAsync(userInput, calendarItemId);
    Task IAppNotificationHandlerHost.HandleCalendarToastJoinOnlineAsync(Guid calendarItemId)
        => HandleCalendarToastJoinOnlineAsync(calendarItemId);
    Task IAppNotificationHandlerHost.HandleMailToastNavigationAsync(Guid mailItemUniqueId)
        => HandleToastNavigationAsync(mailItemUniqueId);
    Task IAppNotificationHandlerHost.HandleMailToastComposeActionAsync(MailOperation action, Guid mailItemUniqueId)
        => HandleToastComposeActionAsync(action, mailItemUniqueId);
    Task IAppNotificationHandlerHost.HandleMailToastBackgroundActionAsync(MailOperation action, Guid mailItemUniqueId)
        => HandleToastActionAsync(action, mailItemUniqueId);
}
