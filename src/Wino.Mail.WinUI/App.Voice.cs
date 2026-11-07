#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI;

/// <summary>
/// The window work of voice plans (<see cref="IVoicePlanHost"/>): the steps that end in front of the user. They follow
/// the notification paths, which already open the shell from the tray and navigate it.
/// </summary>
public partial class App
{
    internal Task OpenVoiceMailAsync(MailCopy mail) => HandleToastNavigationAsync(mail.UniqueId);

    internal Task OpenVoiceEventAsync(Wino.Core.Domain.Entities.Calendar.CalendarItem calendarItem) => HandleCalendarToastNavigationAsync(calendarItem.Id);

    internal async Task OpenVoiceSearchAsync(MailAccount account, IReadOnlyList<IMailItemFolder> folders, MailSearchCriteria criteria)
    {
        await EnsureShellWindowAsync(WinoApplicationMode.Mail, activateWindow: true);

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

    internal async Task OpenVoiceDraftAsync(MailAccount account, MailCopy draft)
    {
        await EnsureShellWindowAsync(WinoApplicationMode.Mail, activateWindow: true);

        var shell = Services.GetRequiredService<MailAppShellViewModel>();
        await ExecuteOnActivationUiThreadAsync(async () =>
        {
            if (shell.MenuItems.TryGetAccountMenuItem(account.Id, out IAccountMenuItem accountMenuItem))
                await shell.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);

            if (shell.MenuItems.TryGetSpecialFolderMenuItem(account.Id, SpecialFolderType.Draft, out var draftFolderMenuItem))
                await shell.NavigateFolderAsync(draftFolderMenuItem);

            Services.GetRequiredService<INavigationService>().Navigate(
                WinoPage.ComposePage,
                new MailItemViewModel(draft) { ShouldFocusComposerOnOpen = true },
                NavigationReferenceFrame.RenderingFrame,
                NavigationTransitionType.DrillIn);
        });
    }

    internal async Task OpenVoiceEventEditorAsync(CalendarEventComposeNavigationArgs args)
    {
        await EnsureShellWindowAsync(WinoApplicationMode.Calendar, activateWindow: true);

        await ExecuteOnActivationUiThreadAsync(() =>
        {
            Services.GetRequiredService<INavigationService>().Navigate(WinoPage.CalendarEventComposePage, args);
            return Task.CompletedTask;
        });
    }
}
