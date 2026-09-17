#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Helpers;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Controls;
using Wino.MenuFlyouts;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.UI;

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

    // Exchange only. Calendar rows carry a context menu whose single entry unpins a public calendar, so
    // for every other calendar the request is swallowed and no menu opens.

    private static AccountCalendarViewModel? ResolveCalendar(object sender)
        => (sender as FrameworkElement)?.DataContext switch
        {
            AccountCalendarViewModel calendar => calendar,
            UngroupedCalendarMenuItem { Parameter: { } calendar } => calendar,
            _ => null
        };

    // Exchange only: a pinned public calendar can be unpinned; every other calendar has no menu. The
    // favourite service announces the change, and the calendar pane drops the calendar and reloads.
    private void CalendarContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement target || ResolveCalendar(sender) is not { IsPublicFolder: true } calendar)
        {
            args.Handled = true;
            return;
        }

        WinoContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            CreateContextCommand(
                Translator.PublicFolders_Unpin,
                WinoIconGlyph.UnPin,
                "CalendarUnpinPublicCalendar",
                new RelayCommand(() => WinoApplication.Current.Services.GetService<IPublicFolderFavoriteService>()?
                    .RemoveFavorite(calendar.AccountId, calendar.RemoteCalendarId)))
        ]);
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

    private void AccountContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: IAccountNavigationMenuItem account } target)
            return;

        var items = new List<ContextFlyoutMenuEntry>
        {
            CreateContextCommand(
                Translator.AccountContextMenu_ManageAccountSettings,
                WinoIconGlyph.ManageAccounts,
                "AccountContextManageSettings",
                new RelayCommand(() => OpenAccountSettings(account)))
        };

        if (account.SupportsAccountSynchronization)
        {
            items.Add(CreateContextCommand(
                Translator.Buttons_Sync,
                WinoIconGlyph.Sync,
                "AccountContextSynchronize",
                new AsyncRelayCommand(account.SynchronizeAccountAsync)));
        }

        if (account is AccountMenuItem mailAccount && account.SupportsMailAccountActions)
        {
            items.Add(CreateContextCommand(
                Translator.AccountContextMenu_CreateFolder,
                WinoIconGlyph.CreateFolder,
                "AccountContextCreateFolder",
                new AsyncRelayCommand(() => MailClient.CreateRootFolderAsync(mailAccount))));
        }

        AddExchangeAccountEntries(items, account);

        WinoContextFlyoutHelper.Show(target, args, items);
    }

    // Exchange only: inbox rules, the junk email lists, and the two switches for the read-only remote
    // trees. The switches are app-wide (the same values as on the account details page), and a change
    // rebuilds the account's folder list.
    private static void AddExchangeAccountEntries(List<ContextFlyoutMenuEntry> items, IAccountNavigationMenuItem accountMenuItem)
    {
        if (accountMenuItem.Account is not { } account ||
            !(accountMenuItem.SupportsExchangeAccountActions || accountMenuItem.SupportsJunkEmailSettings))
        {
            return;
        }

        items.Add(ContextFlyoutSeparatorEntry.Instance);

        if (accountMenuItem.SupportsExchangeAccountActions)
        {
            items.Add(new ContextFlyoutCommandEntry
            {
                Text = Translator.Rules_Title,
                Icon = new ContextFlyoutIcon("\uE71C"),
                Command = new AsyncRelayCommand(() => WinoApplication.Current.Services.GetRequiredService<IMailDialogService>().ShowInboxRulesManagerAsync(account)),
                AutomationId = "AccountContextInboxRules"
            });
        }

        if (accountMenuItem.SupportsJunkEmailSettings)
        {
            items.Add(CreateContextCommand(
                Translator.SettingsJunkEmail_Title,
                WinoIconGlyph.SpecialFolderJunk,
                "AccountContextJunkEmail",
                new RelayCommand(() => OpenJunkEmailSettings(account))));
        }

        if (accountMenuItem.SupportsExchangeAccountActions &&
            WinoApplication.Current.Services.GetService<IPublicFolderFavoriteService>() is { } remoteFolders)
        {
            items.Add(new ContextFlyoutToggleEntry
            {
                Text = Translator.AccountDetailsPage_ShowPublicFolders_Title,
                Icon = CreateContextIcon(WinoIconGlyph.Folder),
                IsChecked = remoteFolders.ArePublicFoldersVisible,
                Command = new RelayCommand(() =>
                {
                    remoteFolders.ArePublicFoldersVisible = !remoteFolders.ArePublicFoldersVisible;
                    WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(account.Id));
                }),
                AutomationId = "AccountContextShowPublicFolders"
            });

            items.Add(new ContextFlyoutToggleEntry
            {
                Text = Translator.AccountDetailsPage_ShowOnlineArchive_Title,
                Icon = CreateContextIcon(WinoIconGlyph.SpecialFolderArchive),
                IsChecked = remoteFolders.AreOnlineArchivesVisible,
                Command = new RelayCommand(() =>
                {
                    remoteFolders.AreOnlineArchivesVisible = !remoteFolders.AreOnlineArchivesVisible;
                    WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(account.Id));
                }),
                AutomationId = "AccountContextShowOnlineArchive"
            });
        }
    }

    // Opens the junk email lists of the account inside Settings, with manage accounts and the
    // account itself as breadcrumb parents so Back behaves as if the user had walked there.
    private static void OpenJunkEmailSettings(Wino.Core.Domain.Entities.Shared.MailAccount account)
    {
        var route = SettingsNavigationRoute.ForAccountSubpage(
            account,
            Translator.SettingsJunkEmail_Title,
            WinoPage.JunkEmailSettingsPage,
            AccountDetailsTab.Mail);

        NavigationService.ChangeApplicationMode(
            WinoApplicationMode.Settings,
            new ShellModeActivationContext
            {
                Parameter = new SettingsPageActivationContext(WinoPage.JunkEmailSettingsPage, account.Id, route),
                SuppressStartupFlows = true
            });
    }

    // Exchange only: a public mail, contact or calendar folder can be pinned and unpinned.
    private void RemoteFolderContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: RemoteFolderMenuItem { CanPin: true } folder } target)
        {
            args.Handled = true;
            return;
        }

        WinoContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            CreateContextCommand(
                folder.PinActionText,
                folder.IsPinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin,
                "RemoteFolderContextPin",
                folder.TogglePinCommand)
        ]);
    }

    private void ContactListContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ContactFilterViewModel contactList } target)
            return;

        if (contactList.IsPublicFolder)
        {
            WinoContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
            [
                CreateContextCommand(
                    Translator.PublicFolders_Unpin,
                    WinoIconGlyph.UnPin,
                    "ContactsPaneUnpinPublicFolder",
                    contactList.UnpinCommand)
            ]);
            return;
        }

        if (!contactList.CanRenameOrDelete)
        {
            args.Handled = true;
            return;
        }

        WinoContextFlyoutHelper.Show(target, args, (ContextFlyoutMenuEntry[])
        [
            CreateContextCommand(
                Translator.ContactList_Rename,
                WinoIconGlyph.Rename,
                "ContactsPaneRenameList",
                contactList.RenameListCommand),
            CreateContextCommand(
                Translator.ContactsPage_Delete,
                WinoIconGlyph.Delete,
                "ContactsPaneDeleteList",
                contactList.DeleteListCommand,
                isDestructive: true,
                shortcut: new ContextFlyoutShortcut("Delete", "Delete"))
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
                WinoIconGlyph.New,
                "ToDoGroupNewList",
                new AsyncRelayCommand(() => group.NewListRequested(group))));
        }

        if (group.IsEditable && group.RenameRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_RenameGroup,
                WinoIconGlyph.Rename,
                "ToDoGroupRename",
                new AsyncRelayCommand(() => group.RenameRequested(group))));
        }

        if (group.CanUngroup && group.UngroupRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_UngroupLists,
                WinoIconGlyph.Move,
                "ToDoGroupUngroupLists",
                new AsyncRelayCommand(() => group.UngroupRequested(group))));
        }

        if (group.CanDelete && group.DeleteRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_DeleteGroup,
                WinoIconGlyph.Delete,
                "ToDoGroupDelete",
                new AsyncRelayCommand(() => group.DeleteRequested(group)),
                isDestructive: true,
                shortcut: new ContextFlyoutShortcut("Delete", "Delete")));
        }

        WinoContextFlyoutHelper.Show(target, args, items);
    }

    private void TaskListContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: AccountTaskListMenuItem list } target)
            return;

        var items = new List<ContextFlyoutMenuEntry>();
        if (list.CanRename && list.RenameRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_RenameList,
                WinoIconGlyph.Rename,
                "ToDoListRename",
                new AsyncRelayCommand(() => list.RenameRequested(list))));
        }

        if (list.IsGrouped && list.RemoveFromGroupRequested is not null)
        {
            items.Add(CreateContextCommand(
                Translator.ToDoPage_RemoveFromGroup,
                WinoIconGlyph.Move,
                "ToDoListRemoveFromGroup",
                new AsyncRelayCommand(() => list.RemoveFromGroupRequested(list))));
        }

        if (list.CanMoveToGroup && list.MoveToGroupRequested is not null)
        {
            var destinations = list.AvailableGroups
                .Where(group => group.Id != list.Parameter.GroupId)
                .Select(group => (ContextFlyoutMenuEntry)CreateContextCommand(
                    group.Title,
                    WinoIconGlyph.Folder,
                    $"ToDoMoveToGroup_{group.Id:N}",
                    new AsyncRelayCommand(() => list.MoveToGroupRequested(list, group.Id))))
                .ToArray();

            if (destinations.Length > 0)
            {
                items.Add(new ContextFlyoutSubMenuEntry
                {
                    Text = Translator.ToDoPage_MoveToGroup,
                    Icon = CreateContextIcon(WinoIconGlyph.Move),
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
                WinoIconGlyph.Delete,
                "ToDoListDelete",
                new AsyncRelayCommand(() => list.DeleteRequested(list)),
                isDestructive: true,
                shortcut: new ContextFlyoutShortcut("Delete", "Delete")));
        }

        WinoContextFlyoutHelper.Show(target, args, items);
    }

    private static ContextFlyoutCommandEntry CreateContextCommand(
        string text,
        WinoIconGlyph icon,
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

    private static ContextFlyoutIcon? CreateContextIcon(WinoIconGlyph icon)
        => ControlConstants.WinoIconFontDictionary.TryGetValue(icon, out var glyph)
            ? new ContextFlyoutIcon(glyph)
            : null;

    private static void OpenAccountSettings(IAccountNavigationMenuItem accountMenuItem)
    {
        NavigationService.ChangeApplicationMode(
            WinoApplicationMode.Settings,
            new ShellModeActivationContext
            {
                Parameter = new SettingsPageActivationContext(
                    WinoPage.ManageAccountsPage,
                    new AccountDetailsNavigationContext(accountMenuItem.Account.Id, accountMenuItem.AccountDetailsTab)),
                SuppressStartupFlows = true
            });
    }

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

        flyout.ShowAt(menuItem, WinoContextFlyoutHelper.CreatePointerAlignedOptions(position));

        var operation = await completionSource.Task;
        flyout.Dispose();

        if (operation != null)
        {
            await mailClient.PerformFolderOperationAsync(operation.Operation, baseFolderMenuItem);
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
