#nullable enable
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Mails;

namespace Wino.Mail.ViewModels.Activation;

/// <summary>
/// <see cref="INotificationResponseNavigator"/> over the head's <see cref="IApplicationShellHost"/> and the mail shell.
/// The mail shell is resolved when needed so a notification never creates it early.
/// </summary>
public sealed class NotificationResponseNavigator(
    IServiceProvider services,
    IApplicationShellHost host,
    INavigationService navigation,
    IActivationStateService activationState,
    IDispatcher dispatcher) : INotificationResponseNavigator
{
    private readonly ILogger _logger = Log.ForContext<NotificationResponseNavigator>();

    private MailAppShellViewModel MailShell => services.GetRequiredService<MailAppShellViewModel>();

    public Task OpenModeAsync(WinoApplicationMode mode, object? parameter = null) => host.EnsureShellAsync(mode, parameter);

    /// <summary>App.HandleToastNavigationAsync: a running mail shell navigates now; a new or switching shell consumes the launch parameter after its menu is rebuilt.</summary>
    public async Task OpenMailAsync(Guid accountId, Guid folderId, MailCopy mailItem)
    {
        var mailReady = host.IsShellActive(WinoApplicationMode.Mail);
        if (!mailReady) activationState.LaunchParameter = new AccountMenuItemExtended(folderId, mailItem);

        if (!await host.EnsureShellAsync(WinoApplicationMode.Mail) || !mailReady) return;

        var shell = MailShell;
        await RunOnUIAsync(async () =>
        {
            var navigated = false;
            if (shell.MenuItems.TryGetFolderMenuItem(folderId, out var folderMenuItem))
            {
                await shell.NavigateFolderAsync(folderMenuItem);
                navigated = true;
            }
            else if (shell.MenuItems.TryGetAccountMenuItem(accountId, out var accountMenuItem))
            {
                await shell.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);
                if (shell.MenuItems.TryGetFolderMenuItem(folderId, out folderMenuItem))
                {
                    await shell.NavigateFolderAsync(folderMenuItem);
                    navigated = true;
                }
            }

            if (navigated) WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(mailItem.UniqueId, ScrollToItem: true));
            else _logger.Information("Notification folder for mail {Id} was not found.", mailItem.UniqueId);
        });
    }

    public async Task<bool> ShowDraftsAsync(Guid accountId)
    {
        if (!await host.EnsureShellAsync(WinoApplicationMode.Mail)) return false;

        var shell = MailShell;
        await RunOnUIAsync(async () =>
        {
            if (shell.MenuItems.TryGetAccountMenuItem(accountId, out var accountMenuItem))
                await shell.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);
            if (shell.MenuItems.TryGetSpecialFolderMenuItem(accountId, SpecialFolderType.Draft, out var draftFolderMenuItem))
                await shell.NavigateFolderAsync(draftFolderMenuItem);
        });

        return true;
    }

    public Task OpenComposerAsync(MailCopy draft)
        => RunOnUIAsync(() =>
        {
            navigation.Navigate(WinoPage.ComposePage, new MailItemViewModel(draft) { ShouldFocusComposerOnOpen = true },
                NavigationReferenceFrame.RenderingFrame, NavigationTransitionType.DrillIn);
            return Task.CompletedTask;
        });

    private async Task RunOnUIAsync(Func<Task> action)
    {
        Task inner = Task.CompletedTask;
        await dispatcher.ExecuteOnUIThread(() => inner = action());
        await inner;
    }
}
