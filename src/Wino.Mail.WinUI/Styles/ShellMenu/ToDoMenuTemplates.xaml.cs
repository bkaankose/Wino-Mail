using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;

namespace Wino.Mail.WinUI.Styles.ShellMenu;

public sealed partial class ToDoMenuTemplates
{
    private IMenuItem _draggedItem;

    public ToDoMenuTemplates() => InitializeComponent();

    private static T MenuItem<T>(object sender) where T : class, IMenuItem
        => (sender as FrameworkElement)?.Tag as T ?? (sender as FrameworkElement)?.DataContext as T;

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
        _draggedItem = (sender as FrameworkElement)?.DataContext as IMenuItem;
        if (_draggedItem is null)
        {
            args.Cancel = true;
            return;
        }
        args.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void ShellItem_DragOver(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as IMenuItem;
        if (_draggedItem is not null && target is not null && !ReferenceEquals(_draggedItem, target) && SameAccount(_draggedItem, target))
            e.AcceptedOperation = DataPackageOperation.Move;
    }

    private async void ShellItem_Drop(object sender, DragEventArgs e)
    {
        var target = (sender as FrameworkElement)?.DataContext as IMenuItem;
        var source = _draggedItem;
        _draggedItem = null;
        if (source is null || target is null || ReferenceEquals(source, target) || !SameAccount(source, target))
            return;

        var handler = target switch
        {
            AccountTaskListGroupMenuItem group => group.DropRequested,
            AccountTaskListMenuItem list => list.DropRequested,
            _ => null
        };
        if (handler is not null)
        {
            var element = sender as FrameworkElement;
            var insertAfter = element is not null && e.GetPosition(element).Y > element.ActualHeight / 2;
            await handler(source, target, insertAfter);
        }
    }

    private static bool SameAccount(IMenuItem first, IMenuItem second)
        => GetAccountId(first) is { } accountId && accountId == GetAccountId(second);

    private static Guid? GetAccountId(IMenuItem item) => item switch
    {
        AccountTaskListGroupMenuItem group => group.Parameter.MailAccountId,
        AccountTaskListMenuItem list => list.Parameter.MailAccountId,
        _ => null
    };
}
