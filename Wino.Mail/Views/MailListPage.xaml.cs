using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using MoreLinq;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Wino.Controls;
using Wino.Controls.Advanced;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Views.Abstract;

namespace Wino.Views
{
    public sealed partial class MailListPage : MailListPageAbstract,
        IRecipient<ResetSingleMailItemSelectionEvent>,
        IRecipient<ClearMailSelectionsRequested>,
        IRecipient<ActiveMailItemChangedEvent>,
        IRecipient<ActiveMailFolderChangedEvent>,
        IRecipient<SelectMailItemContainerEvent>,
        IRecipient<ShellStateUpdated>,
        IRecipient<DisposeRenderingFrameRequested>
    {
        private const double RENDERING_COLUMN_MIN_WIDTH = 375;

        private IStatePersistanceService StatePersistenceService { get; } = App.Current.Services.GetService<IStatePersistanceService>();
        private IKeyPressService KeyPressService { get; } = App.Current.Services.GetService<IKeyPressService>();

        public MailListPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Bindings.Update();

            // Delegate to ViewModel.
            if (e.Parameter is NavigateMailFolderEventArgs folderNavigationArgs)
            {
                WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folderNavigationArgs.BaseFolderMenuItem, folderNavigationArgs.FolderInitLoadAwaitTask));
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            this.Bindings.StopTracking();

            RenderingFrame.Navigate(typeof(IdlePage));

            GC.Collect();
        }

        private void UpdateSelectAllButtonStatus()
        {
            // Check all checkbox if all is selected.
            // Unhook events to prevent selection overriding.

            SelectAllCheckbox.Checked -= SelectAllCheckboxChecked;
            SelectAllCheckbox.Unchecked -= SelectAllCheckboxUnchecked;

            SelectAllCheckbox.IsChecked = MailListView.Items.Count > 0 && MailListView.SelectedItems.Count == MailListView.Items.Count;

            SelectAllCheckbox.Checked += SelectAllCheckboxChecked;
            SelectAllCheckbox.Unchecked += SelectAllCheckboxUnchecked;
        }

        private void SelectionModeToggleChecked(object sender, RoutedEventArgs e)
        {
            ChangeSelectionMode(ListViewSelectionMode.Multiple);
        }

        private void FolderPivotChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var addedItem in e.AddedItems)
            {
                if (addedItem is FolderPivotViewModel pivotItem)
                {
                    pivotItem.IsSelected = true;
                }
            }

            foreach (var removedItem in e.RemovedItems)
            {
                if (removedItem is FolderPivotViewModel pivotItem)
                {
                    pivotItem.IsSelected = false;
                }
            }

            SelectAllCheckbox.IsChecked = false;
            SelectionModeToggle.IsChecked = false;

            MailListView.ClearSelections();

            UpdateSelectAllButtonStatus();
            ViewModel.SelectedPivotChangedCommand.Execute(null);
        }

        private void ChangeSelectionMode(ListViewSelectionMode mode)
        {
            MailListView.ChangeSelectionMode(mode);

            if (ViewModel?.PivotFolders != null)
            {
                ViewModel.PivotFolders.ForEach(a => a.IsExtendedMode = mode == ListViewSelectionMode.Extended);
            }
        }

        private void SelectionModeToggleUnchecked(object sender, RoutedEventArgs e)
        {
            ChangeSelectionMode(ListViewSelectionMode.Extended);
        }

        private void SelectAllCheckboxChecked(object sender, RoutedEventArgs e)
        {
            MailListView.SelectAllWino();
        }

        private void SelectAllCheckboxUnchecked(object sender, RoutedEventArgs e)
        {
            MailListView.ClearSelections();
        }

        void IRecipient<ResetSingleMailItemSelectionEvent>.Receive(ResetSingleMailItemSelectionEvent message)
        {
            // Single item in thread selected.
            // Force main list view to unselect all items, except for the one provided.

            MailListView.ClearSelections(message.SelectedViewModel);
        }

        private async void MailItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            // Context is requested from a single mail point, but we might have multiple selected items.
            // This menu should be calculated based on all selected items by providers.

            if (sender is MailItemDisplayInformationControl control && args.TryGetPosition(sender, out Point p))
            {
                await FocusManager.TryFocusAsync(control, FocusState.Keyboard);

                if (control.DataContext is IMailItem clickedMailItemContext)
                {
                    var targetItems = ViewModel.GetTargetMailItemViewModels(clickedMailItemContext);
                    var availableActions = ViewModel.GetAvailableMailActions(targetItems);

                    if (!availableActions?.Any() ?? false) return;
                    var t = targetItems.ElementAt(0);

                    ViewModel.ChangeCustomFocusedState(targetItems, true);

                    var clickedOperation = await GetMailOperationFromFlyoutAsync(availableActions, control, p.X, p.Y);

                    ViewModel.ChangeCustomFocusedState(targetItems, false);

                    if (clickedOperation == null) return;

                    var prepRequest = new MailOperationPreperationRequest(clickedOperation.Operation, targetItems.Select(a => a.MailCopy));

                    await ViewModel.ExecuteMailOperationAsync(prepRequest);
                }
            }
        }

        private async Task<MailOperationMenuItem> GetMailOperationFromFlyoutAsync(IEnumerable<MailOperationMenuItem> availableActions,
                                                                                  UIElement showAtElement,
                                                                                  double x,
                                                                                  double y)
        {
            var source = new TaskCompletionSource<MailOperationMenuItem>();

            var flyout = new MailOperationFlyout(availableActions, source);

            flyout.ShowAt(showAtElement, new FlyoutShowOptions()
            {
                ShowMode = FlyoutShowMode.Standard,
                Position = new Point(x + 30, y - 20)
            });

            return await source.Task;
        }

        void IRecipient<ClearMailSelectionsRequested>.Receive(ClearMailSelectionsRequested message)
        {
            MailListView.ClearSelections(null, preserveThreadExpanding: true);
        }

        void IRecipient<ActiveMailItemChangedEvent>.Receive(ActiveMailItemChangedEvent message)
        {
            // No active mail item. Go to empty page.
            if (message.SelectedMailItemViewModel == null)
            {
                WeakReferenceMessenger.Default.Send(new CancelRenderingContentRequested());
            }
            else
            {
                // Navigate to composing page.
                if (message.SelectedMailItemViewModel.IsDraft)
                {
                    NavigationTransitionType composerPageTransition = NavigationTransitionType.None;

                    // Dispose active rendering if there is any and go to composer.
                    if (IsRenderingPageActive())
                    {
                        // Prepare WebView2 animation from Rendering to Composing page.
                        PrepareRenderingPageWebViewTransition();

                        // Dispose existing HTML content from rendering page webview.
                        WeakReferenceMessenger.Default.Send(new CancelRenderingContentRequested());
                    }
                    else if (IsComposingPageActive())
                    {
                        // Composer is already active. Prepare composer WebView2 animation.
                        PrepareComposePageWebViewTransition();
                    }
                    else
                        composerPageTransition = NavigationTransitionType.DrillIn;

                    ViewModel.NavigationService.NavigateCompose(message.SelectedMailItemViewModel, composerPageTransition);
                }
                else
                {
                    // Find the MIME and go to rendering page.

                    if (message.SelectedMailItemViewModel == null) return;

                    if (IsComposingPageActive())
                    {
                        PrepareComposePageWebViewTransition();
                    }

                    ViewModel.NavigationService.NavigateRendering(message.SelectedMailItemViewModel);
                }
            }

            UpdateAdaptiveness();
        }

        private bool IsRenderingPageActive() => RenderingFrame.Content is MailRenderingPage;
        private bool IsComposingPageActive() => RenderingFrame.Content is ComposePage;

        private void PrepareComposePageWebViewTransition()
        {
            var webView = GetComposerPageWebView();

            if (webView != null)
            {
                var animation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("WebViewConnectedAnimation", webView);
                animation.Configuration = new BasicConnectedAnimationConfiguration();
            }
        }

        private void PrepareRenderingPageWebViewTransition()
        {
            var webView = GetRenderingPageWebView();

            if (webView != null)
            {
                var animation = ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("WebViewConnectedAnimation", webView);
                animation.Configuration = new BasicConnectedAnimationConfiguration();
            }
        }

        #region Connected Animation Helpers

        private WebView2 GetRenderingPageWebView()
        {
            if (RenderingFrame.Content is MailRenderingPage renderingPage)
                return renderingPage.GetWebView();

            return null;
        }

        private WebView2 GetComposerPageWebView()
        {
            if (RenderingFrame.Content is ComposePage composePage)
                return composePage.GetWebView();

            return null;
        }

        #endregion

        public void Receive(ActiveMailFolderChangedEvent message)
        {
            UpdateAdaptiveness();
        }

        public async void Receive(SelectMailItemContainerEvent message)
        {
            if (message.SelectedMailViewModel == null) return;

            await ViewModel.ExecuteUIThread(async () =>
            {
                MailListView.ClearSelections(message.SelectedMailViewModel, true);

                int retriedSelectionCount = 0;
            trySelection:

                bool isSelected = MailListView.SelectMailItemContainer(message.SelectedMailViewModel);

                if (!isSelected)
                {
                    for (int i = retriedSelectionCount; i < 5;)
                    {
                        // Retry with delay until the container is realized. Max 1 second.
                        await Task.Delay(200);

                        retriedSelectionCount++;

                        goto trySelection;
                    }
                }

                // Automatically scroll to the selected item.
                // This is useful when creating draft.
                if (isSelected && message.ScrollToItem)
                {
                    var collectionContainer = ViewModel.MailCollection.GetMailItemContainer(message.SelectedMailViewModel.UniqueId);

                    // Scroll to thread if available.
                    if (collectionContainer.ThreadViewModel != null)
                    {
                        MailListView.ScrollIntoView(collectionContainer.ThreadViewModel, ScrollIntoViewAlignment.Default);
                    }
                    else if (collectionContainer.ItemViewModel != null)
                    {
                        MailListView.ScrollIntoView(collectionContainer.ItemViewModel, ScrollIntoViewAlignment.Default);
                    }

                }
            });
        }

        public void Receive(ShellStateUpdated message)
        {
            UpdateAdaptiveness();
        }

        private void SearchBoxFocused(object sender, RoutedEventArgs e)
        {
            SearchBar.PlaceholderText = string.Empty;
        }

        private void SearchBarUnfocused(object sender, RoutedEventArgs e)
        {
            SearchBar.PlaceholderText = Translator.SearchBarPlaceholder;
        }

        private void ProcessMailItemKeyboardAccelerator(UIElement sender, ProcessKeyboardAcceleratorEventArgs args)
        {
            if (args.Key == Windows.System.VirtualKey.Delete)
            {
                args.Handled = true;

                ViewModel?.ExecuteMailOperationCommand?.Execute(MailOperation.SoftDelete);
            }
        }

        /// <summary>
        /// Thread header is mail info display control and it can be dragged spearately out of ListView.
        /// We need to prepare a drag package for it from the items inside.
        /// </summary>
        private void ThreadHeaderDragStart(UIElement sender, DragStartingEventArgs args)
        {
            if (sender is MailItemDisplayInformationControl control
                && control.ConnectedExpander?.Content is WinoListView contentListView)
            {
                var allItems = contentListView.Items.Where(a => a is IMailItem);

                // Highlight all items.
                allItems.Cast<MailItemViewModel>().ForEach(a => a.IsCustomFocused = true);

                // Set native drag arg properties.
                args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

                var dragPackage = new MailDragPackage(allItems.Cast<IMailItem>());

                args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);
                args.DragUI.SetContentFromDataPackage();

                control.ConnectedExpander.IsExpanded = true;
            }
        }

        private void ThreadHeaderDragFinished(UIElement sender, DropCompletedEventArgs args)
        {
            if (sender is MailItemDisplayInformationControl control && control.ConnectedExpander != null && control.ConnectedExpander.Content is WinoListView contentListView)
            {
                contentListView.Items.Where(a => a is MailItemViewModel).Cast<MailItemViewModel>().ForEach(a => a.IsCustomFocused = false);
            }
        }

        private async void LeftSwipeItemInvoked(Microsoft.UI.Xaml.Controls.SwipeItem sender, Microsoft.UI.Xaml.Controls.SwipeItemInvokedEventArgs args)
        {
            // Delete item for now.

            var swipeControl = args.SwipeControl;

            swipeControl.Close();

            if (swipeControl.Tag is MailItemViewModel mailItemViewModel)
            {
                var package = new MailOperationPreperationRequest(MailOperation.SoftDelete, mailItemViewModel.MailCopy);
                await ViewModel.ExecuteMailOperationAsync(package);
            }
            else if (swipeControl.Tag is ThreadMailItemViewModel threadMailItemViewModel)
            {
                var package = new MailOperationPreperationRequest(MailOperation.SoftDelete, threadMailItemViewModel.GetMailCopies());
                await ViewModel.ExecuteMailOperationAsync(package);
            }
        }

        private async void RightSwipeItemInvoked(Microsoft.UI.Xaml.Controls.SwipeItem sender, Microsoft.UI.Xaml.Controls.SwipeItemInvokedEventArgs args)
        {
            // Toggle status only for now.

            var swipeControl = args.SwipeControl;

            swipeControl.Close();

            if (swipeControl.Tag is MailItemViewModel mailItemViewModel)
            {
                var operation = mailItemViewModel.IsRead ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead;
                var package = new MailOperationPreperationRequest(operation, mailItemViewModel.MailCopy);

                await ViewModel.ExecuteMailOperationAsync(package);
            }
            else if (swipeControl.Tag is ThreadMailItemViewModel threadMailItemViewModel)
            {
                bool isAllRead = threadMailItemViewModel.ThreadItems.All(a => a.IsRead);

                var operation = isAllRead ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead;
                var package = new MailOperationPreperationRequest(operation, threadMailItemViewModel.GetMailCopies());

                await ViewModel.ExecuteMailOperationAsync(package);
            }
        }

        private void PullToRefreshRequested(Microsoft.UI.Xaml.Controls.RefreshContainer sender, Microsoft.UI.Xaml.Controls.RefreshRequestedEventArgs args)
        {
            ViewModel.SyncFolderCommand?.Execute(null);
        }

        private async void SearchBar_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrWhiteSpace(sender.Text))
            {
                await ViewModel.PerformSearchAsync();
            }
        }

        public void Receive(DisposeRenderingFrameRequested message)
        {
            ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame, NavigationTransitionType.DrillIn);
        }

        private void PageSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ViewModel.MaxMailListLength = e.NewSize.Width - RENDERING_COLUMN_MIN_WIDTH;

            StatePersistenceService.IsReaderNarrowed = e.NewSize.Width < StatePersistenceService.MailListPaneLength + RENDERING_COLUMN_MIN_WIDTH;

            UpdateAdaptiveness();
        }

        private void MailListSizerManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            StatePersistenceService.MailListPaneLength = ViewModel.MailListLength;
        }

        private void UpdateAdaptiveness()
        {
            bool isMultiSelectionEnabled = ViewModel.IsMultiSelectionModeEnabled || KeyPressService.IsCtrlKeyPressed();

            if (StatePersistenceService.IsReaderNarrowed)
            {
                if (ViewModel.HasSingleItemSelection && !isMultiSelectionEnabled)
                {
                    VisualStateManager.GoToState(this, "NarrowRenderer", true);
                }
                else
                {
                    VisualStateManager.GoToState(this, "NarrowMailList", true);
                }
            }
            else
            {
                if (ViewModel.HasSingleItemSelection && !isMultiSelectionEnabled)
                {
                    VisualStateManager.GoToState(this, "BothPanelsMailSelected", true);
                }
                else
                {
                    VisualStateManager.GoToState(this, "BothPanelsNoMailSelected", true);
                }
            }
        }
    }
}
