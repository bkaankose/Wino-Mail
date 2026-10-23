#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.MenuItems;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.MailItem;
using MailCal.Core.Domain.Models.Navigation;
using MailCal.Core.Domain.Models.Settings;
using MailCal.Helpers;
using MailCal.Mail.Controls.Core.ContextFlyout;
using MailCal.Mail.ViewModels.Data;
using MailCal.Mail.WinUI.Controls;
using MailCal.MenuFlyouts;
using MailCal.MenuFlyouts.Context;
using WinRT;

namespace MailCal.Mail.WinUI.Styles.ShellMenu;

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
        => MailCalApplication.Current.Services.GetRequiredService<IMailShellClient>();

    private static INavigationService NavigationService
        => MailCalApplication.Current.Services.GetRequiredService<INavigationService>();

    #region Folder drag and drop

    private void ItemDragEnterOnFolder(object sender, DragEventArgs e)
    {
        if (sender is not MailCalNavigationViewItem container || !CanContinueDragDrop(container, e))
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
        if (sender is MailCalNavigationViewItem leavingContainer)
        {
            leavingContainer.IsDraggingItemOver = false;
        }
    }

    private async void ItemDroppedOnFolder(object sender, DragEventArgs e)
    {
        if (sender is not MailCalNavigationViewItem droppedContainer)
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

    private static bool CanContinueDragDrop(MailCalNavigationViewItem interactingContainer, DragEventArgs args)
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

        var draggedAccountIds = folderMenuItem.HandlingFolders.Select(folder => folder.MailAccountId).ToHashSet();

        return ExtractMailCopies(dragPackage).Any(mail => mail.AssignedAccount is { } account && draggedAccountIds.Contains(account.Id));
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

    private void AccountContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: IAccountNavigationMenuItem account } target)
            return;

        var items = new List<ContextFlyoutMenuEntry>
        {
            CreateContextCommand(
                Translator.AccountContextMenu_ManageAccountSettings,
                MailCalIconGlyph.ManageAccounts,
                "AccountContextManageSettings",
                new RelayCommand(() => OpenAccountSettings(account)))
        };

        // A pending sign-in can only make Sync fail, so the menu offers the fix in its place.
        if (account.IsAttentionRequired)
        {
            items.Add(CreateContextCommand(
                Translator.Buttons_FixAccount,
                MailCalIconGlyph.Warning,
                "AccountContextFixAccount",
                new AsyncRelayCommand(() => FixAccountAsync(account))));
        }
        else if (account.SupportsAccountSynchronization)
        {
            items.Add(CreateContextCommand(
                Translator.Buttons_Sync,
                MailCalIconGlyph.Sync,
                "AccountContextSynchronize",
                new AsyncRelayCommand(account.SynchronizeAccountAsync)));
        }

        if (account is AccountMenuItem mailAccount && account.SupportsMailAccountActions)
        {
            items.Add(CreateContextCommand(
                Translator.AccountContextMenu_CreateFolder,
                MailCalIconGlyph.CreateFolder,
                "AccountContextCreateFolder",
                new AsyncRelayCommand(() => MailClient.CreateRootFolderAsync(mailAccount))));

            items.Add(CreateMarkAccountInboxesAsReadCommand(mailAccount));
        }

        MailCalContextFlyoutHelper.Show(target, args, items);
    }

    private void MergedAccountContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: MergedAccountMenuItem mergedAccount } target)
            return;

        MailCalContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            CreateMarkAccountInboxesAsReadCommand(mergedAccount)
        ]);
    }

    private static ContextFlyoutCommandEntry CreateMarkAccountInboxesAsReadCommand(IAccountMenuItem accountMenuItem)
        => CreateContextCommand(
            Translator.FolderOperation_MarkAllAsRead,
            MailCalIconGlyph.MarkRead,
            "AccountContextMarkAllAsRead",
            new AsyncRelayCommand(() => MailClient.MarkAccountInboxesAsReadAsync(accountMenuItem)));

    private void ContactListContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ContactFilterViewModel contactList } target)
            return;

        if (!contactList.CanRenameOrDelete)
        {
            args.Handled = true;
            return;
        }

        var items = new List<ContextFlyoutMenuEntry>();
        if (contactList.IsCategory)
        {
            items.Add(CreateContextCommand(
                Translator.ContactsPage_NewCategory,
                MailCalIconGlyph.New,
                "ContactsPaneNewCategory",
                contactList.NewCategoryCommand));
        }

        items.Add(CreateContextCommand(
            contactList.IsCategory ? Translator.Buttons_Edit : Translator.ContactList_Rename,
            MailCalIconGlyph.Rename,
            "ContactsPaneRenameList",
            contactList.RenameListCommand));
        items.Add(CreateContextCommand(
            Translator.ContactsPage_Delete,
            MailCalIconGlyph.Delete,
            "ContactsPaneDeleteList",
            contactList.DeleteListCommand,
            isDestructive: true,
            shortcut: new ContextFlyoutShortcut("Delete", "Delete")));

        MailCalContextFlyoutHelper.Show(target, args, items);
    }

    private void ContactCategoriesContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ContactCategoriesExpanderMenuItem expander } target ||
            expander.NewCategoryRequested is null)
        {
            return;
        }

        MailCalContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            CreateContextCommand(
                Translator.ContactsPage_NewCategory,
                MailCalIconGlyph.New,
                "ContactsPaneNewCategory",
                new AsyncRelayCommand(expander.NewCategoryRequested))
        ]);
    }

    private void TaskGroupContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: AccountTaskListGroupMenuItem group } target)
            return;

        var items = new List<ContextFlyoutMenuEntry>();
        if (group.NewListRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_NewList,
                MailCalIconGlyph.New,
                "ToDoGroupNewList",
                new AsyncRelayCommand(() => group.NewListRequested(group))));
        }

        if (group.IsEditable && group.RenameRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_RenameGroup,
                MailCalIconGlyph.Rename,
                "ToDoGroupRename",
                new AsyncRelayCommand(() => group.RenameRequested(group))));
        }

        if (group.CanUngroup && group.UngroupRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_UngroupLists,
                MailCalIconGlyph.Move,
                "ToDoGroupUngroupLists",
                new AsyncRelayCommand(() => group.UngroupRequested(group))));
        }

        if (group.CanDelete && group.DeleteRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_DeleteGroup,
                MailCalIconGlyph.Delete,
                "ToDoGroupDelete",
                new AsyncRelayCommand(() => group.DeleteRequested(group)),
                isDestructive: true,
                shortcut: new ContextFlyoutShortcut("Delete", "Delete")));
        }

        MailCalContextFlyoutHelper.Show(target, args, items);
    }

    private void TaskListContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: AccountTaskListMenuItem list } target)
            return;

        var items = new List<ContextFlyoutMenuEntry>();
        if (list.RenameRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_RenameList,
                MailCalIconGlyph.Rename,
                "ToDoListRename",
                new AsyncRelayCommand(() => list.RenameRequested(list))));
        }

        if (list.IsGrouped && list.RemoveFromGroupRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_RemoveFromGroup,
                MailCalIconGlyph.Move,
                "ToDoListRemoveFromGroup",
                new AsyncRelayCommand(() => list.RemoveFromGroupRequested(list))));
        }

        if (list.CanMoveToGroup && list.MoveToGroupRequested is not null)
        {
            var destinations = list.AvailableGroups
                .Where(group => group.Id != list.Parameter.GroupId)
                .Select(group => (ContextFlyoutMenuEntry)CreateContextCommand(
                    group.Title,
                    MailCalIconGlyph.Folder,
                    $"ToDoMoveToGroup_{group.Id:N}",
                    new AsyncRelayCommand(() => list.MoveToGroupRequested(list, group.Id))))
                .ToArray();

            if (destinations.Length > 0)
            {
                items.Add(new ContextFlyoutSubMenuEntry
                {
                    Text = Translator.ToDoPage_MoveToGroup,
                    Icon = CreateContextIcon(MailCalIconGlyph.Move),
                    Items = destinations,
                    AutomationId = "ToDoListMoveToGroup"
                });
            }
        }

        if (list.CanDelete && list.DeleteRequested is not null)
        {
            items.Add(ContextFlyoutSeparatorEntry.Instance);
            items.Add(CreateContextCommand(
                Translator.ToDoPage_DeleteList,
                MailCalIconGlyph.Delete,
                "ToDoListDelete",
                new AsyncRelayCommand(() => list.DeleteRequested(list)),
                isDestructive: true,
                shortcut: new ContextFlyoutShortcut("Delete", "Delete")));
        }

        MailCalContextFlyoutHelper.Show(target, args, items);
    }

    private static ContextFlyoutCommandEntry CreateContextCommand(
        string text,
        MailCalIconGlyph icon,
        string automationId,
        System.Windows.Input.ICommand command,
        bool isDestructive = false,
        ContextFlyoutShortcut? shortcut = null)
        => new()
        {
            Text = text,
            Icon = CreateContextIcon(icon),
            Command = command,
            IsEnabled = command.CanExecute(null),
            IsDestructive = isDestructive,
            Shortcut = shortcut,
            AutomationId = automationId
        };

    private static ContextFlyoutIcon? CreateContextIcon(MailCalIconGlyph icon)
        => icon == MailCalIconGlyph.None
            ? null
            : new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(icon));

    private static void OpenAccountSettings(IAccountNavigationMenuItem accountMenuItem)
    {
        NavigationService.ChangeApplicationMode(
            MailCalApplicationMode.Settings,
            new ShellModeActivationContext
            {
                Parameter = new SettingsPageActivationContext(
                    MailCalPage.ManageAccountsPage,
                    new AccountDetailsNavigationContext(accountMenuItem.Account.Id, accountMenuItem.AccountDetailsTab)),
                SuppressStartupFlows = true
            });
    }

    private async void MenuItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not MailCalNavigationViewItem menuItem ||
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

        flyout.ShowAt(menuItem, MailCalContextFlyoutHelper.CreatePointerAlignedOptions(position));

        var operation = await completionSource.Task;
        flyout.Dispose();

        if (operation != null)
        {
            await mailClient.PerformFolderOperationAsync(operation.Operation, baseFolderMenuItem);
        }
    }

    /// <summary>
    /// The Fix button on the shared account row. Mail, calendar, contacts and To Do rows all
    /// run the mail shell's fix, so every mode signs in again or opens the server settings the
    /// same way.
    /// </summary>
    private async void AttentionIconClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IAccountNavigationMenuItem accountMenuItem })
            return;

        await FixAccountAsync(accountMenuItem);
    }

    private static Task FixAccountAsync(IAccountNavigationMenuItem accountMenuItem)
        => accountMenuItem.Account is { } account
            ? MailClient.HandleAccountAttentionAsync(account)
            : Task.CompletedTask;

    #endregion

    private IMenuItem? _draggedItem;

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private static T? MenuItem<T>(object sender) where T : class, IMenuItem
        => (sender as FrameworkElement)?.Tag as T ?? (sender as FrameworkElement)?.DataContext as T;

    private async void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItem<NewTaskListMenuItem>(sender) is { NewGroupRequested: not null } item)
            await item.NewGroupRequested();
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
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

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
    private bool CanAcceptDrop(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as AccountTaskListGroupMenuItem;
        var source = GetDraggedList();
        return source is not null && target is not null && SameAccount(source, target) && CanDropOnTarget(source, target);
    }

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
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

    [DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
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
        if (sender is MailCalNavigationViewItem item)
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
