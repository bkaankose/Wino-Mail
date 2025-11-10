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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MoreLinq;
using Windows.Foundation;
using Windows.System;
using Wino.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Mail.WinUI.Controls.ListView;
using Wino.Mail.WinUI.Extensions;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.Client.Mails;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class MailListPage : MailListPageAbstract,
    IRecipient<ClearMailSelectionsRequested>,
    IRecipient<ActiveMailItemChangedEvent>,
    IRecipient<SelectMailItemContainerEvent>,
    IRecipient<DisposeRenderingFrameRequested>
{
    private const double RENDERING_COLUMN_MIN_WIDTH = 375;

    private IStatePersistanceService StatePersistenceService { get; } = Core.WinUI.WinoApplication.Current.Services.GetService<IStatePersistanceService>() ?? throw new Exception($"Can't resolve {nameof(KeyPressService)}");
    private IKeyPressService KeyPressService { get; } = Core.WinUI.WinoApplication.Current.Services.GetService<IKeyPressService>() ?? throw new Exception($"Can't resolve {nameof(KeyPressService)}");
    private IKeyboardShortcutService KeyboardShortcutService { get; } = Core.WinUI.WinoApplication.Current.Services.GetService<IKeyboardShortcutService>() ?? throw new Exception($"Can't resolve {nameof(IKeyboardShortcutService)}");
    public MailListPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        Bindings.Update();

        ViewModel.MailCollection.ItemSelectionChanged += WinoMailCollectionSelectionChanged;

        UpdateSelectAllButtonStatus();
        UpdateAdaptiveness();

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

        ViewModel.MailCollection.ItemSelectionChanged -= WinoMailCollectionSelectionChanged;
        SelectAllCheckbox.Checked -= SelectAllCheckboxChecked;
        SelectAllCheckbox.Unchecked -= SelectAllCheckboxUnchecked;

        MailListView.Cleanup();

        RenderingFrame.Navigate(typeof(IdlePage));

        GC.Collect();
    }

    private void UpdateSelectAllButtonStatus()
    {
        // Check all checkbox if all is selected.
        // Unhook events to prevent selection overriding.

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            SelectAllCheckbox.Checked -= SelectAllCheckboxChecked;
            SelectAllCheckbox.Unchecked -= SelectAllCheckboxUnchecked;

            SelectAllCheckbox.IsChecked = ViewModel.MailCollection.IsAllItemsSelected;

            SelectAllCheckbox.Checked += SelectAllCheckboxChecked;
            SelectAllCheckbox.Unchecked += SelectAllCheckboxUnchecked;
        });
    }

    private void SelectionModeToggleChecked(object sender, RoutedEventArgs e) => ChangeSelectionMode(ListViewSelectionMode.Multiple);

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

    private async void SelectAllCheckboxChecked(object sender, RoutedEventArgs e)
    {
        await ViewModel.MailCollection.SelectAllAsync();
    }

    private async void SelectAllCheckboxUnchecked(object sender, RoutedEventArgs e)
    {
        await ViewModel.MailCollection.UnselectAllAsync();
    }

    private void WinoListViewChoosingItemContainer(ListViewBase sender, ChoosingItemContainerEventArgs args)
    {
        if (args.Item is ThreadMailItemViewModel && args.ItemContainer is not WinoThreadMailItemViewModelListViewItem)
        {
            args.ItemContainer = new WinoThreadMailItemViewModelListViewItem()
            {
                Item = args.Item as ThreadMailItemViewModel
            };
        }
        else if (args.Item is MailItemViewModel && args.ItemContainer is not WinoMailItemViewModelListViewItem)
        {
            args.ItemContainer = new WinoMailItemViewModelListViewItem()
            {
                Item = args.Item as MailItemViewModel
            };
        }
    }

    private async void MailItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // Context is requested from a single mail point, but we might have multiple selected items.
        // This menu should be calculated based on all selected items by providers.

        if (sender is MailItemDisplayInformationControl control && args.TryGetPosition(sender, out Point p))
        {
            var targetItems = ViewModel.MailCollection.SelectedItems;
            var availableActions = ViewModel.GetAvailableMailActions(targetItems);

            if (!availableActions?.Any() ?? false) return;

            var clickedOperation = await GetMailOperationFromFlyoutAsync(availableActions, control, p.X, p.Y);

            if (clickedOperation == null) return;

            var prepRequest = new MailOperationPreperationRequest(clickedOperation.Operation, targetItems.Select(a => a.MailCopy));

            await ViewModel.ExecuteMailOperationAsync(prepRequest);
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

    async void IRecipient<ClearMailSelectionsRequested>.Receive(ClearMailSelectionsRequested message)
    {
        await ViewModel.MailCollection.UnselectAllAsync();
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

                ViewModel.NavigationService.Navigate(WinoPage.ComposePage, message.SelectedMailItemViewModel, NavigationReferenceFrame.RenderingFrame, composerPageTransition);
            }
            else
            {
                // Find the MIME and go to rendering page.

                if (message.SelectedMailItemViewModel == null) return;

                if (IsComposingPageActive())
                {
                    PrepareComposePageWebViewTransition();
                }

                ViewModel.NavigationService.Navigate(WinoPage.MailRenderingPage, message.SelectedMailItemViewModel, NavigationReferenceFrame.RenderingFrame);
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

    private WebView2? GetRenderingPageWebView()
    {
        if (RenderingFrame.Content is MailRenderingPage renderingPage)
            return renderingPage.GetWebView();

        return null;
    }

    private WebView2? GetComposerPageWebView()
    {
        if (RenderingFrame.Content is ComposePage composePage)
            return composePage.GetWebView();

        return null;
    }

    #endregion

    public async void Receive(SelectMailItemContainerEvent message)
    {
        if (message.SelectedMailViewModel == null) return;

        await DispatcherQueue.EnqueueAsync(async () =>
        {
            // MailListView.ClearSelections(message.SelectedMailViewModel, true);

            var collectionContainer = await MailListView.GetItemContainersAsync(message.SelectedMailViewModel);

            if (collectionContainer.Item1 == null && collectionContainer.Item2 == null) return;

            // Automatically scroll to the selected item.
            // This is useful when creating draft.

            if (message.ScrollToItem)
            {
                // Scroll to thread if available.
                // Find the item index on the UI. This is different than ListView.

                int scrollIndex = -1;
                if (collectionContainer.Item2 != null)
                {
                    scrollIndex = ViewModel.MailCollection.IndexOf(collectionContainer.Item2.Item);
                }
                else if (collectionContainer.Item1 != null)
                {
                    scrollIndex = ViewModel.MailCollection.IndexOf(collectionContainer.Item1.Item);
                }

                if (scrollIndex >= 0)
                {
                    await MailListView.SmoothScrollIntoViewWithIndexAsync(scrollIndex);
                }
            }

            var listView = collectionContainer.Item3 ?? MailListView;
            var mailItemViewModelContainer = collectionContainer.Item1;
            var threadMailItemViewModelContainer = collectionContainer.Item2;

            await WinoClickItemInternalAsync(listView, collectionContainer.Item1?.Item ?? null);
        });
    }

    private void SearchBoxFocused(object sender, RoutedEventArgs e)
    {
        SearchBar.PlaceholderText = string.Empty;
    }

    private void SearchBarUnfocused(object sender, RoutedEventArgs e)
    {
        SearchBar.PlaceholderText = Translator.SearchBarPlaceholder;
    }

    /// <summary>
    /// Thread header is mail info display control and it can be dragged spearately out of ListView.
    /// We need to prepare a drag package for it from the items inside.
    /// </summary>
    private void ThreadHeaderDragStart(UIElement sender, DragStartingEventArgs args)
    {
        //if (sender is MailItemDisplayInformationControl control
        //    && control.ConnectedExpander?.Content is WinoListView contentListView)
        //{
        //    var allItems = contentListView.Items.Where(a => a is MailCopy);

        //    // Highlight all items.
        //    allItems.Cast<MailItemViewModel>().ForEach(a => a.IsCustomFocused = true);

        //    // Set native drag arg properties.
        //    args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

        //    var dragPackage = new MailDragPackage(allItems.Cast<MailCopy>());

        //    args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);
        //    args.DragUI.SetContentFromDataPackage();

        //    control.ConnectedExpander.IsExpanded = true;
        //}
    }

    private void ThreadHeaderDragFinished(UIElement sender, DropCompletedEventArgs args)
    {

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
            var package = new MailOperationPreperationRequest(MailOperation.SoftDelete, threadMailItemViewModel.ThreadEmails.Select(a => a.MailCopy));
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
            bool isAllRead = threadMailItemViewModel.ThreadEmails.All(a => a.IsRead);

            var operation = isAllRead ? MailOperation.MarkAsUnread : MailOperation.MarkAsRead;
            var package = new MailOperationPreperationRequest(operation, threadMailItemViewModel.ThreadEmails.Select(a => a.MailCopy));

            await ViewModel.ExecuteMailOperationAsync(package);
        }
    }

    private async void SearchBar_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // User clicked 'x' button to clearout the search text.
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrWhiteSpace(sender.Text))
        {
            ViewModel.IsOnlineSearchButtonVisible = false;
            await ViewModel.PerformSearchAsync();
        }
    }

    public void Receive(DisposeRenderingFrameRequested message)
    {
        ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame, NavigationTransitionType.DrillIn);
        UpdateAdaptiveness();
    }

    protected override void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<ClearMailSelectionsRequested>(this);
        WeakReferenceMessenger.Default.Register<ActiveMailItemChangedEvent>(this);
        WeakReferenceMessenger.Default.Register<SelectMailItemContainerEvent>(this);
        WeakReferenceMessenger.Default.Register<DisposeRenderingFrameRequested>(this);
    }

    protected override void UnregisterRecipients()
    {
        WeakReferenceMessenger.Default.Unregister<ClearMailSelectionsRequested>(this);
        WeakReferenceMessenger.Default.Unregister<ActiveMailItemChangedEvent>(this);
        WeakReferenceMessenger.Default.Unregister<SelectMailItemContainerEvent>(this);
        WeakReferenceMessenger.Default.Unregister<DisposeRenderingFrameRequested>(this);
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
        bool isMultiSelectionEnabled = ViewModel.IsMultiSelectionModeEnabled;

        if (StatePersistenceService.IsReaderNarrowed)
        {
            if (ViewModel.MailCollection.HasSingleItemSelected && !isMultiSelectionEnabled)
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
            if (ViewModel.MailCollection.HasSingleItemSelected && !isMultiSelectionEnabled)
            {
                VisualStateManager.GoToState(this, "BothPanelsMailSelected", true);
            }
            else
            {
                VisualStateManager.GoToState(this, "BothPanelsNoMailSelected", true);
            }
        }
    }



    private void WinoMailCollectionSelectionChanged(object? sender, EventArgs args)
    {
        UpdateSelectAllButtonStatus();
        UpdateAdaptiveness();
    }

    private async void WinoListViewProcessKeyboardAccelerators(UIElement sender, ProcessKeyboardAcceleratorEventArgs args)
    {
        args.Handled = true;

        if (args.Key == VirtualKey.Delete)
        {
            ViewModel.ExecuteMailOperationCommand.Execute(MailOperation.SoftDelete);
        }
        else if (args.Key == VirtualKey.A && args.Modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            await ViewModel.MailCollection.ToggleSelectAllAsync();
        }
        else
        {
            // Check keyboard shortcuts from service.
            ModifierKeys modifiers = args.Modifiers.ToDomainModifierKeys();

            var operation = await KeyboardShortcutService.GetMailOperationForKeyAsync(args.Key.ToString(), modifiers);

            if (operation != null)
            {
                ViewModel.ExecuteMailOperationCommand.Execute(operation);
            }
            else
            {
                args.Handled = false;
            }
        }
    }

    private async Task WinoClickItemInternalAsync(WinoListView listView, object? clickedItem)
    {
        if (clickedItem == null) return;

        // Requirements (summary):
        // CTRL pressed -> multi-select behaviour
        //   * Clicking single item toggles only that item.
        //   * Clicking thread header toggles selection of thread AND all its children (all on or all off).
        //   * Clicking an item inside a thread toggles only that child item.
        // CTRL NOT pressed -> single-select (exclusive) with toggle support (can leave zero selected)
        //   * Clicking thread header: unselect everything else, collapse all other threads, select only the thread + first child.
        //       If already in that state (thread selected and first child selected), clicking again unselects all (nothing selected).
        //   * Clicking a single (non-thread) item OR a child item: collapse & unselect all others then toggle that item's selection.
        //       If it was selected, result is nothing selected.

        bool isCtrlPressed = KeyPressService.IsCtrlKeyPressed();

        // Helper local to collapse all other threads (we always collapse ALL then possibly re-expand the active thread per rules)
        async Task CollapseAllThreadsExceptAsync(ThreadMailItemViewModel? except)
        {
            await ViewModel.MailCollection.CollapseAllThreadsAsync();
            if (except != null)
            {
                // We'll expand explicitly when required by logic below.
            }
        }

        if (isCtrlPressed)
        {
            switch (clickedItem)
            {
                case ThreadMailItemViewModel thread:
                    {
                        // Determine if thread + all children currently selected
                        bool allSelected = thread.IsSelected && thread.ThreadEmails.All(e => e.IsSelected);
                        if (allSelected)
                        {
                            // Unselect thread & all children
                            thread.IsSelected = false;
                            foreach (var child in thread.ThreadEmails)
                                child.IsSelected = false;
                        }
                        else
                        {
                            // Select thread & all children (do NOT disturb other selections in CTRL mode)
                            thread.IsSelected = true;
                            foreach (var child in thread.ThreadEmails)
                                child.IsSelected = true;
                            // Keep it expanded so user can see items
                            thread.IsThreadExpanded = true;
                        }
                        break;
                    }
                case MailItemViewModel mail:
                    {
                        // Toggle just this item; no collapse/unselect of others in multi-select mode.
                        mail.IsSelected = !mail.IsSelected;
                        break;
                    }
            }
            return; // Multi-select path ends here.
        }

        // SINGLE-SELECTION (exclusive) MODE WITH TOGGLE SUPPORT
        if (clickedItem is ThreadMailItemViewModel clickedThread)
        {
            bool wasThreadSelected = clickedThread.IsSelected;

            // Reset everything first (exclusive selection scenario)
            await ViewModel.MailCollection.UnselectAllAsync();
            await CollapseAllThreadsExceptAsync(clickedThread);

            if (wasThreadSelected)
            {
                // Toggle off -> leave nothing selected (all unselected, thread collapsed)
                clickedThread.IsThreadExpanded = false;
                return;
            }

            // Select thread + first child only
            clickedThread.IsSelected = true;
            var firstChild = clickedThread.ThreadEmails.FirstOrDefault();
            if (firstChild != null)
            {
                // Ensure only first child selected
                foreach (var child in clickedThread.ThreadEmails)
                {
                    child.IsSelected = child == firstChild;
                }
            }
            clickedThread.IsThreadExpanded = true; // Show contents of active thread
        }
        else if (clickedItem is MailItemViewModel clickedMail)
        {
            bool wasSelected = clickedMail.IsSelected;

            // Determine if this mail belongs to an already selected & expanded thread.
            // If so, we only want to switch the selection inside that thread without collapsing or unselecting the thread header.
            ThreadMailItemViewModel? parentThread = null;

            foreach (var group in ViewModel.MailCollection.MailItems)
            {
                foreach (var item in group)
                {
                    if (item is ThreadMailItemViewModel thread && thread.ThreadEmails.Contains(clickedMail))
                    {
                        parentThread = thread;
                        break;
                    }
                }
                if (parentThread != null) break;
            }

            bool isInSelectedExpandedThread = parentThread != null && parentThread.IsSelected && parentThread.IsThreadExpanded;

            if (isInSelectedExpandedThread)
            {
                // Switch selection within the thread: unselect previously selected children, select the clicked one.
                if (parentThread?.ThreadEmails != null)
                {
                    foreach (var child in parentThread.ThreadEmails)
                    {
                        child.IsSelected = child == clickedMail && !wasSelected; // If clicking an already selected child -> toggle off (none selected in thread except header)
                    }
                }

                if (wasSelected && parentThread != null)
                {
                    // Clicking the already selected child should leave the thread header selected (canonical state: thread + first child previously).
                    // Decide whether to keep a child selected; spec wants toggle off allowed, so leave no child selected.
                    // Ensure parent thread stays selected & expanded.
                    parentThread.IsSelected = true;
                    parentThread.IsThreadExpanded = true;
                }
                return; // Done.
            }

            // Normal single-item (non-thread or entering a thread via child) behavior.
            await ViewModel.MailCollection.UnselectAllAsync();
            await ViewModel.MailCollection.CollapseAllThreadsAsync();

            if (!wasSelected)
            {
                clickedMail.IsSelected = true; // Toggle on
            }
        }
    }

    private async void WinoListViewItemClicked(object sender, ItemClickEventArgs e)
    {
        if (sender is not WinoListView listView) return;

        await WinoClickItemInternalAsync(listView, e.ClickedItem);
    }
}
