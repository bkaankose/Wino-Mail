#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Controls;
using Wino.MenuFlyouts;
using Wino.MenuFlyouts.Context;

namespace Wino.Mail.WinUI.Styles.ShellMenu;

/// <summary>
/// View glue for the mail navigation pane templates: folder drag and drop, the folder
/// operation flyout and the account context menu. It lives with the templates instead of
/// the shell so the shell stays unaware of mail.
/// </summary>
public sealed partial class MailMenuTemplates
{
    public MailMenuTemplates()
    {
        InitializeComponent();
    }

    private static IMailShellClient MailClient
        => WinoApplication.Current.Services.GetRequiredService<IMailShellClient>();

    private static INavigationService NavigationService
        => WinoApplication.Current.Services.GetRequiredService<INavigationService>();

    #region Folder drag and drop

    private void ItemDragEnterOnFolder(object sender, DragEventArgs e)
    {
        if (sender is not WinoNavigationViewItem container || !CanContinueDragDrop(container, e))
            return;

        container.IsDraggingItemOver = true;

        if (container.DataContext is IBaseFolderMenuItem draggingFolder)
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = string.Format(Translator.DragMoveToFolderCaption, draggingFolder.FolderName);
        }
    }

    private void ItemDragLeaveFromFolder(object sender, DragEventArgs e)
    {
        if (sender is WinoNavigationViewItem leavingContainer)
        {
            leavingContainer.IsDraggingItemOver = false;
        }
    }

    private async void ItemDroppedOnFolder(object sender, DragEventArgs e)
    {
        if (sender is not WinoNavigationViewItem droppedContainer)
            return;

        droppedContainer.IsDraggingItemOver = false;

        if (!CanContinueDragDrop(droppedContainer, e) ||
            droppedContainer.DataContext is not IBaseFolderMenuItem draggingFolder)
        {
            return;
        }

        if (e.DataView.Properties[nameof(MailDragPackage)] is not MailDragPackage dragPackage)
            return;

        e.AcceptedOperation = DataPackageOperation.Move;

        await MailClient.PerformMoveOperationAsync(ExtractMailCopies(dragPackage).ToList(), draggingFolder);
    }

    private static bool CanContinueDragDrop(WinoNavigationViewItem interactingContainer, DragEventArgs args)
    {
        if (!args.DataView.Properties.ContainsKey(nameof(MailDragPackage)))
            return false;

        if (args.DataView.Properties[nameof(MailDragPackage)] is not MailDragPackage dragPackage ||
            !dragPackage.DraggingMails.Any())
        {
            return false;
        }

        if (interactingContainer.IsSelected)
            return false;

        if (interactingContainer.DataContext is not IBaseFolderMenuItem folderMenuItem || !folderMenuItem.IsMoveTarget)
            return false;

        var draggedAccountIds = folderMenuItem.HandlingFolders.Select(folder => folder.MailAccountId);
        var draggedMails = ExtractMailCopies(dragPackage).ToList();

        return draggedMails.Count > 0 && draggedMails.Any(mail => draggedAccountIds.Contains(mail.AssignedAccount.Id));
    }

    private static IEnumerable<MailCopy> ExtractMailCopies(MailDragPackage dragPackage)
    {
        foreach (var item in dragPackage.DraggingMails)
        {
            if (item is MailCopy mailCopy)
            {
                yield return mailCopy;
            }
            else if (item is MailItemViewModel singleMailItemViewModel)
            {
                yield return singleMailItemViewModel.MailCopy;
            }
            else if (item is ThreadMailItemViewModel threadViewModel)
            {
                foreach (var threadMail in threadViewModel.ThreadEmails)
                {
                    yield return threadMail.MailCopy;
                }
            }
        }
    }

    #endregion

    #region Context menus

    private async void MenuItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not WinoNavigationViewItem menuItem ||
            menuItem.DataContext is not IBaseFolderMenuItem baseFolderMenuItem ||
            !baseFolderMenuItem.IsMoveTarget ||
            !args.TryGetPosition(sender, out Point position))
        {
            return;
        }

        args.Handled = true;

        var mailClient = MailClient;
        var completionSource = new TaskCompletionSource<FolderOperationMenuItem>();
        var actions = mailClient.GetFolderContextMenuActions(baseFolderMenuItem);
        var flyout = new FolderOperationFlyout(actions, completionSource);

        flyout.ShowAt(menuItem, new FlyoutShowOptions
        {
            ShowMode = FlyoutShowMode.Standard,
            Position = new Point(position.X + 30, position.Y - 20)
        });

        var operation = await completionSource.Task;
        flyout.Dispose();

        if (operation != null)
        {
            await mailClient.PerformFolderOperationAsync(operation.Operation, baseFolderMenuItem);
        }
    }

    private void ManageAccountSettingsMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountMenuItem accountMenuItem })
            return;

        NavigationService.ChangeApplicationMode(
            WinoApplicationMode.Settings,
            new ShellModeActivationContext
            {
                Parameter = new SettingsPageActivationContext(
                    WinoPage.ManageAccountsPage,
                    new AccountDetailsNavigationContext(accountMenuItem.AccountId, AccountDetailsTab.General)),
                SuppressStartupFlows = true
            });
    }

    private async void CreateFolderMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AccountMenuItem accountMenuItem })
        {
            await MailClient.CreateRootFolderAsync(accountMenuItem);
        }
    }

    private async void AttentionIconClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountMenuItem accountMenuItem })
            return;

        if (MailClient is Wino.Mail.ViewModels.MailAppShellViewModel mailClient)
        {
            await mailClient.HandleAccountAttentionAsync(accountMenuItem.Parameter);
        }
    }

    #endregion
}
