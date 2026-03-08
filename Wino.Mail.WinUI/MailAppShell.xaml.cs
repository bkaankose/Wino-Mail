using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;
using Wino.MenuFlyouts;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class MailAppShell : MailAppShellAbstract,
    IRecipient<AccountMenuItemExtended>,
    IRecipient<NavigateMailFolderEvent>,
    IRecipient<CreateNewMailWithMultipleAccountsRequested>
{
    public Frame GetShellFrame() => InnerShellFrame;

    [GeneratedDependencyProperty]
    public partial UIElement? TopShellContent { get; set; }

    public MailAppShell() : base()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        Bindings.StopTracking();
    }

    private async void ItemDroppedOnFolder(object sender, DragEventArgs e)
    {
        // Validate package content.
        if (sender is WinoNavigationViewItem droppedContainer)
        {
            droppedContainer.IsDraggingItemOver = false;

            if (CanContinueDragDrop(droppedContainer, e))
            {
                if (droppedContainer.DataContext is IBaseFolderMenuItem draggingFolder)
                {
                    var dragPackage = e.DataView.Properties[nameof(MailDragPackage)] as MailDragPackage;

                    if (dragPackage == null) return;

                    e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                    var mailCopies = ExtractMailCopies(dragPackage).ToList();

                    await ViewModel.PerformMoveOperationAsync(mailCopies, draggingFolder);
                }
            }
        }
    }

    private void ItemDragLeaveFromFolder(object sender, DragEventArgs e)
    {
        if (sender is WinoNavigationViewItem leavingContainer)
        {
            leavingContainer.IsDraggingItemOver = false;
        }
    }

    private bool CanContinueDragDrop(WinoNavigationViewItem interactingContainer, DragEventArgs args)
    {
        // TODO: Maybe override caption with some information why the validation failed?
        // Note: Caption has a max length. It may be trimmed in some languages.

        if (interactingContainer == null || !args.DataView.Properties.ContainsKey(nameof(MailDragPackage))) return false;

        var dragPackage = args.DataView.Properties[nameof(MailDragPackage)] as MailDragPackage;

        // Invalid package.
        if (dragPackage == null || !dragPackage.DraggingMails.Any()) return false;

        // Check whether source and target folder are the same.
        if (interactingContainer.IsSelected) return false;

        // Check if the interacting container is a folder.
        if (!(interactingContainer.DataContext is IBaseFolderMenuItem folderMenuItem)) return false;

        // Check if the folder is a move target.
        if (!folderMenuItem.IsMoveTarget) return false;

        // Check whether the moving item's account has at least one same as the target folder's account.
        var draggedAccountIds = folderMenuItem.HandlingFolders.Select(a => a.MailAccountId);

        var draggedMails = ExtractMailCopies(dragPackage);

        if (!draggedMails.Any()) return false;
        if (!draggedMails.Any(a => draggedAccountIds.Contains(a.AssignedAccount.Id))) return false;

        return true;
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

    private void ItemDragEnterOnFolder(object sender, DragEventArgs e)
    {
        // Validate package content.
        if (sender is WinoNavigationViewItem droppedContainer && CanContinueDragDrop(droppedContainer, e))
        {
            droppedContainer.IsDraggingItemOver = true;

            var draggingFolder = droppedContainer.DataContext as IBaseFolderMenuItem;

            if (draggingFolder == null) return;

            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
            e.DragUIOverride.Caption = string.Format(Translator.DragMoveToFolderCaption, draggingFolder.FolderName);
        }
    }

    public async void Receive(AccountMenuItemExtended message)
    {
        await DispatcherQueue.EnqueueAsync(async () =>
        {
            if (message.FolderId == default) return;

            if (ViewModel.MenuItems.TryGetFolderMenuItem(message.FolderId, out IBaseFolderMenuItem foundMenuItem))
            {
                foundMenuItem.Expand();

                await ViewModel.NavigateFolderAsync(foundMenuItem);

                navigationView.SelectedItem = foundMenuItem;

                if (message.NavigateMailItem == null) return;

                // At this point folder is navigated and items are loaded.
                WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(message.NavigateMailItem.UniqueId, ScrollToItem: true));
            }
            else if (ViewModel.MenuItems.TryGetAccountMenuItem(message.NavigateMailItem.AssignedAccount.Id, out IAccountMenuItem accountMenuItem))
            {
                // Loaded account is different. First change the folder items and navigate.

                await ViewModel.ChangeLoadedAccountAsync(accountMenuItem, navigateInbox: false);

                // Find the folder.

                if (ViewModel.MenuItems.TryGetFolderMenuItem(message.FolderId, out IBaseFolderMenuItem accountFolderMenuItem))
                {
                    accountFolderMenuItem.Expand();

                    await ViewModel.NavigateFolderAsync(accountFolderMenuItem);

                    navigationView.SelectedItem = accountFolderMenuItem;

                    // At this point folder is navigated and items are loaded.
                    WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(message.NavigateMailItem.UniqueId, ScrollToItem: true));
                }
            }
        });
    }

    private async void MenuSelectionChanged(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is IMenuItem invokedMenuItem)
        {
            await ViewModel.MenuItemInvokedOrSelectedAsync(invokedMenuItem);
        }
    }

    private async void NavigationViewItemInvoked(Microsoft.UI.Xaml.Controls.NavigationView sender, Microsoft.UI.Xaml.Controls.NavigationViewItemInvokedEventArgs args)
    {
        // SelectsOnInvoked is handled in MenuSelectionChanged.
        // This part is only for the items that are not selectable.
        if (args.InvokedItemContainer is WinoNavigationViewItem winoNavigationViewItem)
        {
            if (winoNavigationViewItem.SelectsOnInvoked) return;

            await ViewModel.MenuItemInvokedOrSelectedAsync(winoNavigationViewItem.DataContext as IMenuItem);
        }
    }

    public void Receive(NavigateMailFolderEvent message)
    {
        if (message.BaseFolderMenuItem == null) return;

        if (navigationView.SelectedItem != message.BaseFolderMenuItem)
        {
            var navigateFolderArgs = new NavigateMailFolderEventArgs(message.BaseFolderMenuItem, message.FolderInitLoadAwaitTask);

            ViewModel.NavigationService.Navigate(WinoPage.MailListPage, navigateFolderArgs, NavigationReferenceFrame.InnerShellFrame);

            // Prevent double navigation.
            navigationView.SelectionChanged -= MenuSelectionChanged;
            navigationView.SelectedItem = message.BaseFolderMenuItem;
            navigationView.SelectionChanged += MenuSelectionChanged;
        }
        else
        {
            // Complete the init task since we are already on the right page.
            message.FolderInitLoadAwaitTask?.TrySetResult(true);
        }
    }

    private void ShellFrameContentNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) => TopShellContent = ((BasePage)e.Content).ShellContent;

    partial void OnTopShellContentChanged(UIElement? newValue) => WeakReferenceMessenger.Default.Send(new TitleBarShellContentUpdated());

    private async void MenuItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // Delegate this request to ViewModel.
        // VM will prepare available actions for this folder and show Menu Flyout.

        if (sender is WinoNavigationViewItem menuItem &&
            menuItem.DataContext is IBaseFolderMenuItem baseFolderMenuItem &&
            baseFolderMenuItem.IsMoveTarget &&
            args.TryGetPosition(sender, out Point p))
        {
            args.Handled = true;

            var source = new TaskCompletionSource<FolderOperationMenuItem>();

            var actions = ViewModel.GetFolderContextMenuActions(baseFolderMenuItem);
            var flyout = new FolderOperationFlyout(actions, source);

            flyout.ShowAt(menuItem, new FlyoutShowOptions()
            {
                ShowMode = FlyoutShowMode.Standard,
                Position = new Point(p.X + 30, p.Y - 20)
            });

            var operation = await source.Task;

            flyout.Dispose();

            // No action selected.
            if (operation == null) return;

            await ViewModel.PerformFolderOperationAsync(operation.Operation, baseFolderMenuItem);
        }
    }

    public void Receive(CreateNewMailWithMultipleAccountsRequested message)
    {
        // Find the NewMail menu item container.

        var container = navigationView.ContainerFromMenuItem(ViewModel.CreateMailMenuItem);

        var flyout = new AccountSelectorFlyout(message.AllAccounts, ViewModel.CreateNewMailForAsync);

        flyout.ShowAt(container, new FlyoutShowOptions()
        {
            ShowMode = FlyoutShowMode.Auto,
            Placement = FlyoutPlacementMode.Right
        });
    }

    private void NavigationPaneOpening(Microsoft.UI.Xaml.Controls.NavigationView sender, object args)
    {
        // It's annoying that NavigationView doesn't respect expansion state of the items in Minimal display mode.
        // Expanded items are collaped, and users need to expand them again.
        // Regardless of the reason, we will expand the selected item if it's a folder with parent account for visibility.

        if (sender.DisplayMode == Microsoft.UI.Xaml.Controls.NavigationViewDisplayMode.Minimal && sender.SelectedItem is IFolderMenuItem selectedFolderMenuItem)
        {
            selectedFolderMenuItem.Expand();
        }
    }

    private void NavigationViewDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        if (args.DisplayMode == NavigationViewDisplayMode.Minimal)
        {
            InnerShellFrame.Margin = new Thickness(7, 0, 0, 0);
        }
        else
        {
            InnerShellFrame.Margin = new Thickness(0);
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.RepeatCount > 1 || ShouldIgnoreShortcut())
            return;

        var key = NormalizeKey(e.Key);
        if (string.IsNullOrEmpty(key))
            return;

        var shortcutService = WinoApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>();
        var shortcut = await shortcutService.GetShortcutForKeyAsync(WinoApplicationMode.Mail, key, GetCurrentModifierKeys());

        if (shortcut == null)
            return;

        var details = new KeyboardShortcutTriggerDetails
        {
            ShortcutId = shortcut.Id,
            Mode = shortcut.Mode,
            Action = shortcut.Action,
            Key = shortcut.Key,
            ModifierKeys = shortcut.ModifierKeys,
            Sender = sender,
            Origin = FocusManager.GetFocusedElement(XamlRoot)
        };

        await ViewModel.KeyboardShortcutHook(details);

        if (InnerShellFrame.Content is BasePage activePage && activePage.AssociatedViewModel != null)
        {
            await activePage.AssociatedViewModel.KeyboardShortcutHook(details);
        }

        if (details.Handled)
        {
            e.Handled = true;
        }
    }

    private bool ShouldIgnoreShortcut()
    {
        var focusedElement = FocusManager.GetFocusedElement(XamlRoot);

        if (focusedElement is TextBox or AutoSuggestBox or PasswordBox or RichEditBox or ComboBox)
            return true;

        if (focusedElement is FrameworkElement frameworkElement)
        {
            var typeName = frameworkElement.GetType().Name;
            if (typeName.Contains("WebView", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static ModifierKeys GetCurrentModifierKeys()
    {
        var modifiers = ModifierKeys.None;

        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Control;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Alt;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Shift;
        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            modifiers |= ModifierKeys.Windows;
        }

        return modifiers;
    }

    private static string NormalizeKey(Windows.System.VirtualKey key)
    {
        return key switch
        {
            Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows => string.Empty,
            _ => key.ToString()
        };
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        WeakReferenceMessenger.Default.Register<AccountMenuItemExtended>(this);
        WeakReferenceMessenger.Default.Register<CreateNewMailWithMultipleAccountsRequested>(this);
        WeakReferenceMessenger.Default.Register<NavigateMailFolderEvent>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        WeakReferenceMessenger.Default.Unregister<AccountMenuItemExtended>(this);
        WeakReferenceMessenger.Default.Unregister<CreateNewMailWithMultipleAccountsRequested>(this);
        WeakReferenceMessenger.Default.Unregister<NavigateMailFolderEvent>(this);
    }
}
