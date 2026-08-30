#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
public sealed partial class ShellMenuTemplates
{
    public ShellMenuTemplates()
    {
        InitializeComponent();
    }

    private void UngroupedCalendarCheckBoxTapped(object sender, TappedRoutedEventArgs e)
        => e.Handled = true;

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

    private IMenuItem? _draggedItem;


    private static T? MenuItem<T>(object sender) where T : class, IMenuItem
        => (sender as FrameworkElement)?.Tag as T ?? (sender as FrameworkElement)?.DataContext as T;

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<NewTaskListMenuItem>(sender) is { NewGroupRequested: not null } item)
            await item.NewGroupRequested();
    }

    private async void GroupNewList_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<AccountTaskListGroupMenuItem>(sender) is { NewListRequested: not null } item)
            await item.NewListRequested(item);
    }

    private async void GroupRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<AccountTaskListGroupMenuItem>(sender) is { RenameRequested: not null } item)
            await item.RenameRequested(item);
    }

    private async void GroupUngroup_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<AccountTaskListGroupMenuItem>(sender) is { UngroupRequested: not null } item)
            await item.UngroupRequested(item);
    }

    private async void GroupDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<AccountTaskListGroupMenuItem>(sender) is { DeleteRequested: not null } item)
            await item.DeleteRequested(item);
    }

    private async void ListRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<AccountTaskListMenuItem>(sender) is { RenameRequested: not null } item)
            await item.RenameRequested(item);
    }

    private async void ListRemoveFromGroup_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<AccountTaskListMenuItem>(sender) is { RemoveFromGroupRequested: not null } item)
            await item.RemoveFromGroupRequested(item);
    }

    private async void ListDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<AccountTaskListMenuItem>(sender) is { DeleteRequested: not null } item)
            await item.DeleteRequested(item);
    }

    private void ListFlyout_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout ||
            flyout.Items.OfType<MenuFlyoutItemBase>().Select(candidate => (candidate as FrameworkElement)?.Tag).OfType<AccountTaskListMenuItem>().FirstOrDefault() is not { } item)
            return;

        var remove = flyout.Items.OfType<MenuFlyoutItem>().FirstOrDefault(candidate =>
            AutomationProperties.GetAutomationId(candidate) == "ToDoListRemoveFromGroup");
        if (remove is not null)
            remove.Visibility = item.IsGrouped ? Visibility.Visible : Visibility.Collapsed;

        var move = flyout.Items.OfType<MenuFlyoutSubItem>().FirstOrDefault();
        if (move is null)
            return;

        move.Visibility = item.CanMoveToGroup ? Visibility.Visible : Visibility.Collapsed;
        move.Items.Clear();
        foreach (var group in item.AvailableGroups.Where(group => group.Id != item.Parameter.GroupId))
        {
            var destination = new MenuFlyoutItem
            {
                Text = group.Title,
                Icon = new FontIcon { Glyph = "\uE8B7" }
            };
            AutomationProperties.SetAutomationId(destination, $"ToDoMoveToGroup_{group.Id:N}");
            destination.Click += async (_, _) =>
            {
                if (item.MoveToGroupRequested is not null)
                    await item.MoveToGroupRequested(item, group.Id);
            };
            move.Items.Add(destination);
        }
        move.IsEnabled = move.Items.Count > 0;
    }

    private void ShellItem_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        _draggedItem = (sender as FrameworkElement)?.DataContext as AccountTaskListMenuItem;
        if (_draggedItem is not AccountTaskListMenuItem { CanMoveToGroup: true } list)
        {
            args.Cancel = true;
            return;
        }

        args.AllowedOperations = DataPackageOperation.Move;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.Properties.Title = list.Title;
        args.Data.SetText(list.Title);
        args.DragUI.SetContentFromDataPackage();
    }

    private void ShellItem_DragEnter(object sender, DragEventArgs e)
    {
        if (CanAcceptDrop(sender, e))
            SetDropTargetState(sender, true);
    }

    private void ShellItem_DragLeave(object sender, DragEventArgs e)
        => SetDropTargetState(sender, false);

    private bool CanAcceptDrop(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as AccountTaskListGroupMenuItem;
        var source = GetDraggedList();
        return source is not null && target is not null && SameAccount(source, target) && CanDropOnTarget(source, target);
    }

    private void ShellItem_DragOver(object sender, DragEventArgs e)
    {
        if (CanAcceptDrop(sender, e))
        {
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = ((sender as FrameworkElement)?.DataContext as AccountTaskListGroupMenuItem)?.Title;
            e.DragUIOverride.IsCaptionVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void ShellItem_Drop(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as AccountTaskListGroupMenuItem;
        var source = GetDraggedList();
        SetDropTargetState(sender, false);
        _draggedItem = null;
        if (source is null || target is null || !SameAccount(source, target) || !CanDropOnTarget(source, target))
            return;

        if (target.DropRequested is not null)
            await target.DropRequested(source, target, false);
    }

    private AccountTaskListMenuItem? GetDraggedList()
        => _draggedItem as AccountTaskListMenuItem;

    private static void SetDropTargetState(object sender, bool value)
    {
        if (sender is WinoNavigationViewItem item)
            item.IsDraggingItemOver = value;
    }

    private static bool SameAccount(IMenuItem first, IMenuItem second)
        => GetAccountId(first) is { } accountId && accountId == GetAccountId(second);

    private static bool CanDropOnTarget(IMenuItem source, IMenuItem target)
        => source is AccountTaskListMenuItem { CanMoveToGroup: true } list &&
           target is AccountTaskListGroupMenuItem group &&
           list.Parameter.GroupId != group.Parameter.Id;

    private static Guid? GetAccountId(IMenuItem item) => item switch
    {
        AccountTaskListGroupMenuItem group => group.Parameter.MailAccountId,
        AccountTaskListMenuItem list => list.Parameter.MailAccountId,
        _ => null
    };
}
