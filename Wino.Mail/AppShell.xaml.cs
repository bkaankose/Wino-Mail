using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Wino.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Messages.Accounts;
using Wino.Core.Messages.Mails;
using Wino.Core.Messages.Shell;
using Wino.Extensions;
using Wino.Mail.ViewModels.Data;
using Wino.MenuFlyouts;
using Wino.MenuFlyouts.Context;
using Wino.Views.Abstract;

namespace Wino.Views
{
    public sealed partial class AppShell : AppShellAbstract,
        IRecipient<AccountMenuItemExtended>,
        IRecipient<NavigateMailFolderEvent>,
        IRecipient<CreateNewMailWithMultipleAccountsRequested>,
        IRecipient<InfoBarMessageRequested>
    {
        public AppShell() : base()
        {
            InitializeComponent();

            var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
            coreTitleBar.LayoutMetricsChanged += TitleBarLayoutUpdated;
        }

        private void TitleBarLayoutUpdated(CoreApplicationViewTitleBar sender, object args) => UpdateTitleBarLayout(sender);

        private void UpdateTitleBarLayout(CoreApplicationViewTitleBar coreTitleBar) => RealAppBar.SystemReserved = coreTitleBar.SystemOverlayRightInset;

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
                        var mailCopies = new List<MailCopy>();

                        var dragPackage = e.DataView.Properties[nameof(MailDragPackage)] as MailDragPackage;
                        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

                        // Extract mail copies from IMailItem.
                        // ThreadViewModels will be divided into pieces.

                        foreach (var item in dragPackage.DraggingMails)
                        {
                            if (item is MailItemViewModel singleMailItemViewModel)
                            {
                                mailCopies.Add(singleMailItemViewModel.MailCopy);
                            }
                            else if (item is ThreadMailItemViewModel threadViewModel)
                            {
                                mailCopies.AddRange(threadViewModel.GetMailCopies());
                            }
                        }

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
            if (!dragPackage.DraggingMails.Any()) return false;

            // Check whether source and target folder are the same.
            if (interactingContainer.IsSelected) return false;

            // Check if the interacting container is a folder.
            if (!(interactingContainer.DataContext is IBaseFolderMenuItem folderMenuItem)) return false;

            // Check if the folder is a move target.
            if (!folderMenuItem.IsMoveTarget) return false;

            // Check whether the moving item's account has at least one same as the target folder's account.
            var draggedAccountIds = folderMenuItem.HandlingFolders.Select(a => a.MailAccountId);

            if (!dragPackage.DraggingMails.Any(a => draggedAccountIds.Contains(a.AssignedAccount.Id))) return false;

            return true;
        }

        private void ItemDragEnterOnFolder(object sender, DragEventArgs e)
        {
            // Validate package content.
            if (sender is WinoNavigationViewItem droppedContainer && CanContinueDragDrop(droppedContainer, e))
            {
                droppedContainer.IsDraggingItemOver = true;

                var draggingFolder = droppedContainer.DataContext as IBaseFolderMenuItem;

                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
                e.DragUIOverride.Caption = string.Format(Translator.DragMoveToFolderCaption, draggingFolder.FolderName);
            }
        }

        public async void Receive(AccountMenuItemExtended message)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.High, async () =>
            {
                if (message.FolderId == default) return;

                var menuItem = ViewModel.MenuItems.GetFolderItem(message.FolderId);

                if (menuItem == null) return;

                menuItem.Expand();

                await ViewModel.NavigateFolderAsync(menuItem);

                navigationView.SelectedItem = menuItem;

                if (message.NavigateMailItem == null) return;

                // At this point folder is navigated and items are loaded.
                WeakReferenceMessenger.Default.Send(new MailItemNavigationRequested(message.NavigateMailItem.UniqueId));
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

                ViewModel.NavigationService.NavigateFolder(navigateFolderArgs);

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

        private void ShellFrameContentNavigated(object sender, Windows.UI.Xaml.Navigation.NavigationEventArgs e)
            => RealAppBar.ShellFrameContent = (e.Content as BasePage).ShellContent;

        private void BackButtonClicked(Controls.Advanced.WinoAppTitleBar sender, RoutedEventArgs args)
        {
            WeakReferenceMessenger.Default.Send(new ClearMailSelectionsRequested());
        }

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

        /// <summary>
        /// InfoBar message is requested.
        /// </summary>
        public async void Receive(InfoBarMessageRequested message)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (string.IsNullOrEmpty(message.ActionButtonTitle) || message.Action == null)
                {
                    ShellInfoBar.ActionButton = null;
                }
                else
                {
                    ShellInfoBar.ActionButton = new Button()
                    {
                        Content = message.ActionButtonTitle,
                        Command = new RelayCommand(message.Action)
                    };
                }

                ShellInfoBar.Message = message.Message;
                ShellInfoBar.Title = message.Title;
                ShellInfoBar.Severity = message.Severity.AsMUXCInfoBarSeverity();
                ShellInfoBar.IsOpen = true;
            });
        }
    }
}
