using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Mails;

namespace Wino.Mail.ViewModels;

/// <summary>
/// Menu-driven navigation requests that used to be serviced by the shell's code-behind.
/// They only ever needed the mail menu and the navigation service, so they live with the
/// mail mode view model now.
/// </summary>
public partial class MailAppShellViewModel :
    IRecipient<AccountMenuItemExtended>,
    IRecipient<NavigateMailFolderEvent>
{
    /// <summary>
    /// Expands the account tree down to a folder and optionally scrolls to a mail in it.
    /// Used by notification activations and by search results.
    /// </summary>
    public async void Receive(AccountMenuItemExtended message)
    {
        if (message.FolderId == default && message.NavigateMailItem == null)
            return;

        await ExecuteUIThreadAsync(async () =>
        {
            if (message.FolderId != default &&
                MenuItems.TryGetFolderMenuItem(message.FolderId, out IBaseFolderMenuItem foundMenuItem))
            {
                await ExpandAndNavigateAsync(foundMenuItem, message.NavigateMailItem);
                return;
            }

            if (message.NavigateMailItem == null)
                return;

            if (!MenuItems.TryGetAccountMenuItem(message.NavigateMailItem.AssignedAccount.Id, out IAccountMenuItem accountMenuItem))
                return;

            await ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);

            if (MenuItems.TryGetFolderMenuItem(message.FolderId, out IBaseFolderMenuItem accountFolderMenuItem))
            {
                await ExpandAndNavigateAsync(accountFolderMenuItem, message.NavigateMailItem);
            }
        });
    }

    private async Task ExpandAndNavigateAsync(IBaseFolderMenuItem folderMenuItem, Core.Domain.Entities.Mail.MailCopy navigateMailItem)
    {
        folderMenuItem.Expand();

        await NavigateFolderAsync(folderMenuItem);

        SelectedMenuItem = folderMenuItem;

        if (navigateMailItem != null)
        {
            Messenger.Send(new MailItemNavigationRequested(navigateMailItem.UniqueId, ScrollToItem: true));
        }
    }

    /// <summary>
    /// Selects a folder in the pane and shows it. Already-selected folders simply complete
    /// the caller's wait handle instead of re-navigating.
    /// </summary>
    public void Receive(NavigateMailFolderEvent message)
    {
        if (message.BaseFolderMenuItem == null)
            return;

        if (ReferenceEquals(SelectedMenuItem, message.BaseFolderMenuItem))
        {
            message.FolderInitLoadAwaitTask?.TrySetResult(true);
            return;
        }

        _ = ExecuteUIThread(() =>
        {
            var navigateFolderArgs = new NavigateMailFolderEventArgs(message.BaseFolderMenuItem, message.FolderInitLoadAwaitTask);

            NavigationService.Navigate(WinoPage.MailListPage, navigateFolderArgs, NavigationReferenceFrame.InnerShellFrame);

            SelectedMenuItem = message.BaseFolderMenuItem;
        });
    }
}
