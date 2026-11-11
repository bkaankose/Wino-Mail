#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Agent.Runtime.Plan;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services.Assistant;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI;

/// <summary>
/// The window work of voice plans (<see cref="IAgentPlanHost"/>): the steps that end in front of the user. They follow
/// the notification paths, which already open the shell from the tray and navigate it.
/// </summary>
public partial class App
{
    internal Task OpenVoiceMailAsync(MailCopy mail) => HandleToastNavigationAsync(mail.UniqueId);

    internal Task OpenVoiceEventAsync(Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem) => HandleCalendarToastNavigationAsync(calendarItem.Id);

    internal async Task OpenVoiceSearchAsync(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSearchCriteria criteria)
    {
        await EnsureMailShellReadyAsync();

        await ExecuteOnActivationUiThreadAsync(async () =>
        {
            var shell = Services.GetRequiredService<MailAppShellViewModel>();
            if (shell.MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem))
                await shell.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);

            // The list loads the folder before it can search it; a search sent earlier would be replaced by the load.
            var folderMenuItem = folders
                .Select(folder => shell.MenuItems.TryGetFolderMenuItem(folder.Id, out IBaseFolderMenuItem item) ? item : null)
                .FirstOrDefault(static item => item is not null);
            if (folderMenuItem is null && shell.MenuItems.TryGetSpecialFolderMenuItem(account.Id, SpecialFolderType.Inbox, out var inbox))
                folderMenuItem = inbox;

            if (folderMenuItem is not null)
            {
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await shell.NavigateFolderAsync(folderMenuItem, loaded);
                await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        });

        WeakReferenceMessenger.Default.Send(new MailSearchRequested(criteria, folders));
    }

    /// <summary>
    /// Shows the account's Drafts with the new draft selected, which opens it in the composer. The list has already
    /// reacted to the new draft in whatever folder it showed; selecting it there is what keeps the composer open.
    /// </summary>
    internal async Task OpenVoiceDraftAsync(MailAccount account, MailCopy draft)
    {
        await EnsureMailShellReadyAsync();

        var shell = Services.GetRequiredService<MailAppShellViewModel>();
        await ExecuteOnActivationUiThreadAsync(async () =>
        {
            if (shell.MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem))
                await shell.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);

            if (shell.MenuItems.TryGetSpecialFolderMenuItem(account.Id, SpecialFolderType.Draft, out var draftFolderMenuItem))
            {
                var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await shell.NavigateFolderAsync(draftFolderMenuItem, loaded);
                await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        });

        WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(draft.UniqueId, ScrollToItem: true));
    }

    /// <summary>
    /// Brings up the mail shell. Coming from another mode, its activation rebuilds the menu and opens the default
    /// Inbox asynchronously; the assistant's navigation waits for that, or the Inbox replaces it.
    /// </summary>
    private async Task EnsureMailShellReadyAsync()
    {
        var wasMailMode = HasShellWindow()
            && Services.GetRequiredService<IStatePersistanceService>().ApplicationMode == WinoApplicationMode.Mail;

        await EnsureShellWindowAsync(WinoApplicationMode.Mail, activateWindow: true);
        if (wasMailMode)
            return;

        await WaitForShellModeNavigationAsync();
        var initialization = Services.GetRequiredService<MailAppShellViewModel>().NavigationInitialization;
        await Task.WhenAny(initialization, Task.Delay(TimeSpan.FromSeconds(10)));
    }

    internal async Task OpenVoiceEventEditorAsync(CalendarEventComposeNavigationArgs args)
    {
        await EnsureShellWindowAsync(WinoApplicationMode.Calendar, activateWindow: true);
        await WaitForShellModeNavigationAsync();

        await ExecuteOnActivationUiThreadAsync(() =>
        {
            Services.GetRequiredService<INavigationService>().Navigate(WinoPage.CalendarEventComposePage, args);
            return Task.CompletedTask;
        });
    }

    /// <summary>A rule the assistant created, opened in the settings' rule editor.</summary>
    internal Task OpenVoiceMailRuleAsync(Guid accountId, Guid filterId)
        => OpenVoiceSettingsPageAsync(WinoPage.MailFilterEditorPage, new MailFilterEditorNavigationParameter(accountId, filterId));

    /// <summary>A page of the app: a mode of the shell, or an account's mail rules in the settings.</summary>
    internal async Task OpenVoiceViewAsync(AgentView view, Guid? accountId)
    {
        var mode = view switch
        {
            AgentView.Calendar => WinoApplicationMode.Calendar,
            AgentView.Tasks => WinoApplicationMode.Tasks,
            AgentView.Contacts => WinoApplicationMode.Contacts,
            AgentView.Settings or AgentView.MailRules => WinoApplicationMode.Settings,
            _ => WinoApplicationMode.Mail,
        };
        var account = view == AgentView.MailRules
            ? accountId ?? (await Services.GetRequiredService<IAccountService>().GetAccountsAsync()).FirstOrDefault()?.Id
            : null;
        if (account is { } id)
        {
            await OpenVoiceSettingsPageAsync(WinoPage.MailFiltersPage, id);
            return;
        }

        await EnsureShellWindowAsync(mode, activateWindow: true);
    }

    /// <summary>
    /// A settings sub-page with its parameter. Navigating to the page itself would open its root page (Manage
    /// Accounts) instead, so the settings page receives it as its activation target.
    /// </summary>
    private async Task OpenVoiceSettingsPageAsync(WinoPage page, object parameter)
    {
        await EnsureShellWindowAsync(WinoApplicationMode.Settings, activateWindow: true);
        await WaitForShellModeNavigationAsync();
        await ExecuteOnActivationUiThreadAsync(() =>
        {
            Services.GetRequiredService<INavigationService>().Navigate(WinoPage.SettingsPage, new SettingsPageActivationContext(page, parameter));
            return Task.CompletedTask;
        });
    }
}
