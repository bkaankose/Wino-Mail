using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Collections;
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
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Controls.ListView;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Services;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.Client.Mails;
using Wino.Views.Abstract;
using WinRT;

// object, IMailListItem
// Register generic types from CommunityToolkit.Mvvm for AOT compatibility when passed across WinRT boundary
[assembly: GeneratedWinRTExposedExternalType(typeof(ObservableGroup<object, IMailListItem>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ReadOnlyObservableGroup<object, IMailListItem>))]
[assembly: GeneratedWinRTExposedExternalType(typeof(ReadOnlyObservableGroupedCollection<object, IMailListItem>))]

namespace Wino.Views.Mail;

public sealed partial class MailListPage : MailListPageAbstract,
    IRecipient<ClearMailSelectionsRequested>,
    IRecipient<ActiveMailItemChangedEvent>,
    IRecipient<SelectMailItemContainerEvent>,
    IRecipient<DisposeRenderingFrameRequested>,
    IHostedPopoutSource,
    ITitleBarSearchHost
{
    private const double RENDERING_COLUMN_MIN_WIDTH = 375;
    private const int SELECTION_SETTLE_DELAY_MS = 120;
    private int _idleNavigationRequestVersion = 0;
    private int _mailActivationRequestVersion = 0;
    private IPopoutClient? _activePopoutClient;
    private readonly Dictionary<FrameworkElement, HostedContentPopoutWindow> _hostedPopoutWindows = [];
    private PendingHostedPopoutNavigation? _pendingHostedPopoutNavigation;

    private IStatePersistanceService StatePersistenceService { get; } = WinoApplication.Current.Services.GetService<IStatePersistanceService>() ?? throw new Exception($"Can't resolve {nameof(KeyPressService)}");
    private IKeyPressService KeyPressService { get; } = WinoApplication.Current.Services.GetService<IKeyPressService>() ?? throw new Exception($"Can't resolve {nameof(KeyPressService)}");
    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
    public string SearchText
    {
        get => ViewModel.SearchQuery;
        set => ViewModel.SearchQuery = value;
    }

    public string SearchPlaceholderText => Translator.SearchBarPlaceholder;

    public MailListPage()
    {
        InitializeComponent();
        RenderingFrame.Navigated += RenderingFrame_Navigated;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        Bindings.Update();

        if (ViewModel.ActiveFolder != null)
        {
            ViewModel.StatePersistenceService.CoreWindowTitle = $"{ViewModel.ActiveFolder.AssignedAccountName} - {ViewModel.ActiveFolder.FolderName}";
        }

        ViewModel.MailCollection.ItemSelectionChanged += WinoMailCollectionSelectionChanged;
        MailListView.MailDragStateChanged += MailListViewMailDragStateChanged;

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

        InvalidatePendingIdleNavigation();
        InvalidatePendingMailActivation();
        DetachPopoutClient();

        this.Bindings.StopTracking();

        ViewModel.MailCollection.ItemSelectionChanged -= WinoMailCollectionSelectionChanged;
        MailListView.MailDragStateChanged -= MailListViewMailDragStateChanged;
        SelectAllCheckbox.Checked -= SelectAllCheckboxChecked;
        SelectAllCheckbox.Unchecked -= SelectAllCheckboxUnchecked;
        ViewModel.SetDragState(false);

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

    private void MailItemDisplayInformationControl_HoverActionExecuted(object sender, MailOperationPreperationRequest e)
    {
        ViewModel.ExecuteHoverActionCommand.Execute(e);
    }

    private async void FolderPivotChanged(object sender, SelectionChangedEventArgs e)
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

        if (ViewModel.MailCollection.SelectedItemsCount > 0)
        {
            await ViewModel.MailCollection.UnselectAllAsync();
        }

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
            IEnumerable<MailItemViewModel> targetItems;

            if (control.ActionItem is ThreadMailItemViewModel threadItem)
            {
                await SelectThreadForContextMenuAsync(threadItem);
            }
            else if (control.ActionItem is MailItemViewModel mailItem && !ViewModel.MailCollection.SelectedItems.Contains(mailItem))
            {
                // Right clicked item is not selected. Select.
                await WinoClickItemInternalAsync(mailItem, true);
            }

            // Default to all selected items.
            targetItems = ViewModel.MailCollection.SelectedItems;
            var areAllPinned = targetItems.Any() && targetItems.All(item => item.MailCopy.IsPinned);
            var availableActions = ViewModel.GetAvailableMailActions(targetItems);
            var (availableCategories, assignedCategoryIds) = await ViewModel.GetAvailableCategoriesAsync(targetItems);

            var clickedAction = await GetMailContextActionFromFlyoutAsync(
                availableActions,
                availableCategories,
                assignedCategoryIds,
                areAllPinned,
                control,
                p.X,
                p.Y);

            if (clickedAction == null) return;

            if (clickedAction.PinState.HasValue)
            {
                await ViewModel.ChangePinnedStatusAsync(targetItems, clickedAction.PinState.Value);
                return;
            }

            if (clickedAction.Category != null)
            {
                await ViewModel.ToggleCategoryAssignmentAsync(clickedAction.Category, targetItems, clickedAction.IsCategoryAssignedToAll);
                return;
            }

            if (clickedAction.Operation == null)
                return;

            var prepRequest = new MailOperationPreperationRequest(clickedAction.Operation.Operation, targetItems.Select(a => a.MailCopy));

            await ViewModel.ExecuteMailOperationAsync(prepRequest);
        }
    }

    private async Task SelectThreadForContextMenuAsync(ThreadMailItemViewModel threadItem)
    {
        bool isThreadFullySelected = threadItem.IsSelected && threadItem.ThreadEmails.All(a => a.IsSelected);
        bool hasSelectionsOutsideThread = ViewModel.MailCollection.SelectedItems.Any(a => !threadItem.ThreadEmails.Contains(a));

        // No-op to avoid visual collapse/re-expand flicker on right-click.
        if (threadItem.IsThreadExpanded && isThreadFullySelected && !hasSelectionsOutsideThread)
        {
            return;
        }

        // Context menu on a thread should target the whole thread and keep it expanded.
        await ViewModel.MailCollection.ExecuteSelectionBatchAsync(() =>
        {
            foreach (var group in ViewModel.MailCollection.MailItems)
            {
                foreach (var item in group)
                {
                    if (item is ThreadMailItemViewModel thread)
                    {
                        thread.IsSelected = ReferenceEquals(thread, threadItem);
                        thread.IsThreadExpanded = ReferenceEquals(thread, threadItem);

                        foreach (var threadMail in thread.ThreadEmails)
                        {
                            threadMail.IsSelected = ReferenceEquals(thread, threadItem);
                        }
                    }
                    else if (item is MailItemViewModel mailItem)
                    {
                        mailItem.IsSelected = false;
                    }
                }
            }
        });
    }

    private async Task<MailContextAction?> GetMailContextActionFromFlyoutAsync(
        IEnumerable<MailOperationMenuItem> availableActions,
        IReadOnlyList<MailCategory> availableCategories,
        IReadOnlyCollection<Guid> assignedCategoryIds,
        bool areAllPinned,
        UIElement showAtElement,
        double x,
        double y)
    {
        var source = new TaskCompletionSource<MailContextAction?>();
        var flyout = new WinoMenuFlyout();

        foreach (var action in availableActions ?? [])
        {
            if (action.Operation == MailOperation.Seperator)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                continue;
            }

            var menuFlyoutItem = new MailOperationMenuFlyoutItem(action, clicked =>
            {
                source.TrySetResult(new MailContextAction(clicked));
                flyout.Hide();
            });

            flyout.Items.Add(menuFlyoutItem);
        }

        if (flyout.Items.Count > 0 && flyout.Items.LastOrDefault() is not MenuFlyoutSeparator)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var pinItem = new MenuFlyoutItem
        {
            Text = areAllPinned ? Translator.FolderOperation_Unpin : Translator.FolderOperation_Pin,
            Icon = new WinoFontIcon { Icon = areAllPinned ? WinoIconGlyph.UnPin : WinoIconGlyph.Pin }
        };

        MenuFlyoutLanguageHelper.Apply(pinItem);

        pinItem.Click += (_, _) =>
        {
            source.TrySetResult(new MailContextAction(!areAllPinned));
            flyout.Hide();
        };

        flyout.Items.Add(pinItem);

        if (availableCategories?.Count > 0)
        {
            if (flyout.Items.LastOrDefault() is not MenuFlyoutSeparator)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
            }

            var categorySubItem = new MenuFlyoutSubItem
            {
                Text = Translator.MailCategoryMenuItem
            };

            foreach (var category in availableCategories)
            {
                var wasAssignedToAll = assignedCategoryIds.Contains(category.Id);
                var categoryItem = new ToggleMenuFlyoutItem
                {
                    Text = category.Name,
                    IsChecked = wasAssignedToAll
                };

                categoryItem.Click += (_, _) =>
                {
                    source.TrySetResult(new MailContextAction(category, wasAssignedToAll));
                    flyout.Hide();
                };

                categorySubItem.Items.Add(categoryItem);
            }

            flyout.Items.Add(categorySubItem);
        }

        flyout.Closing += (_, _) => source.TrySetResult(null);

        flyout.ShowAt(showAtElement, new FlyoutShowOptions()
        {
            ShowMode = FlyoutShowMode.Standard,
            Position = new Point(x + 30, y - 20)
        });

        return await source.Task;
    }

    private sealed record MailContextAction(MailOperationMenuItem? Operation = null, MailCategory? Category = null, bool IsCategoryAssignedToAll = false, bool? PinState = null)
    {
        public MailContextAction(MailCategory category, bool isCategoryAssignedToAll) : this((MailOperationMenuItem?)null, category, isCategoryAssignedToAll)
        {
        }

        public MailContextAction(bool pinState) : this((MailOperationMenuItem?)null, (MailCategory?)null, false, pinState)
        {
        }
    }

    async void IRecipient<ClearMailSelectionsRequested>.Receive(ClearMailSelectionsRequested message)
    {
        await ViewModel.MailCollection.UnselectAllAsync();
    }

    void IRecipient<ActiveMailItemChangedEvent>.Receive(ActiveMailItemChangedEvent message)
    {
        int requestVersion = ++_mailActivationRequestVersion;

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (requestVersion != _mailActivationRequestVersion) return;

            ApplyActiveMailItemChange(message.SelectedMailItemViewModel);
        });
    }

    private void ApplyActiveMailItemChange(MailItemViewModel? selectedMailItemViewModel)
    {
        // No active mail item. Go to empty page.
        if (selectedMailItemViewModel == null)
        {
            _ = NavigateIdleWhenSelectionSettlesAsync();
        }
        else
        {
            InvalidatePendingIdleNavigation();

            // Navigate to composing page.
            if (selectedMailItemViewModel.IsDraft)
            {
                NavigationTransitionType composerPageTransition = NavigationTransitionType.None;

                // Dispose active rendering if there is any and go to composer.
                if (IsRenderingPageActive())
                {
                    // Prepare WebView2 animation from Rendering to Composing page.
                    PrepareRenderingPageWebViewTransition();

                    // Dispose existing HTML content from rendering page webview.
                    if (RenderingFrame.Content is MailRenderingPage renderingPage)
                    {
                        _ = renderingPage.ClearRenderedContentAsync();
                    }
                }
                else if (IsComposingPageActive())
                {
                    // Composer is already active. Skip connected animation since the page
                    // will be reused in-place (no navigation occurs).
                    // NavigationService will send ReaderItemRefreshRequestedEvent instead.
                }
                else
                    composerPageTransition = NavigationTransitionType.DrillIn;

                ViewModel.NavigationService.Navigate(WinoPage.ComposePage, selectedMailItemViewModel, NavigationReferenceFrame.RenderingFrame, composerPageTransition);
            }
            else
            {
                // Find the MIME and go to rendering page.

                if (IsComposingPageActive())
                {
                    PrepareComposePageWebViewTransition();
                }

                ViewModel.NavigationService.Navigate(WinoPage.MailRenderingPage, selectedMailItemViewModel, NavigationReferenceFrame.RenderingFrame);
            }
        }

        UpdateAdaptiveness();
    }

    private bool IsRenderingPageActive() => RenderingFrame.Content is MailRenderingPage;
    private bool IsComposingPageActive() => RenderingFrame.Content is ComposePage;

    private void RenderingFrame_Navigated(object sender, NavigationEventArgs e)
    {
        AttachPopoutClient(RenderingFrame.Content as IPopoutClient);

        if (_pendingHostedPopoutNavigation != null
            && TryGetPendingHostedPopoutTarget(RenderingFrame.Content, _pendingHostedPopoutNavigation, out var hostedContent))
        {
            _ = ContinuePendingHostedPopoutNavigationAsync(hostedContent, _pendingHostedPopoutNavigation);
        }
    }

    private void AttachPopoutClient(IPopoutClient? client)
    {
        if (ReferenceEquals(_activePopoutClient, client))
            return;

        DetachPopoutClient();

        _activePopoutClient = client;
        if (_activePopoutClient != null)
        {
            _activePopoutClient.PopOutRequested += ActivePopoutClient_PopOutRequested;
            _activePopoutClient.HostActionRequested += ActivePopoutClient_HostActionRequested;
        }
    }

    private void DetachPopoutClient()
    {
        if (_activePopoutClient != null)
        {
            _activePopoutClient.PopOutRequested -= ActivePopoutClient_PopOutRequested;
            _activePopoutClient.HostActionRequested -= ActivePopoutClient_HostActionRequested;
            _activePopoutClient = null;
        }
    }

    private async void ActivePopoutClient_PopOutRequested(object? sender, PopOutRequestedEventArgs e)
    {
        await HostedContentPopoutCoordinator.PopOutCurrentContentAsync(this);
    }

    private void ActivePopoutClient_HostActionRequested(object? sender, PopoutHostActionRequestedEventArgs e)
    {
        if (sender is FrameworkElement content)
        {
            HandleHostedClientAction(content, e);
        }
    }

    private void InvalidatePendingIdleNavigation()
    {
        unchecked
        {
            _idleNavigationRequestVersion++;
        }
    }

    private void InvalidatePendingMailActivation()
    {
        unchecked
        {
            _mailActivationRequestVersion++;
        }
    }

    private async Task NavigateIdleWhenSelectionSettlesAsync()
    {
        int requestVersion = ++_idleNavigationRequestVersion;

        await Task.Delay(SELECTION_SETTLE_DELAY_MS);

        if (requestVersion != _idleNavigationRequestVersion) return;
        if (ViewModel.MailCollection.SelectedItemsCount != 0) return;

        if (IsRenderingPageActive())
        {
            if (RenderingFrame.Content is MailRenderingPage renderingPage)
            {
                _ = renderingPage.ClearRenderedContentAsync();
            }
        }

        // Ensure rendering frame actually navigates away from Compose/Rendering pages.
        // Otherwise those pages keep their messenger registrations alive.
        ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame, NavigationTransitionType.DrillIn);
        UpdateAdaptiveness();
    }

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
        if (message.MailUniqueId == Guid.Empty) return;

        // Find the item from the collection.
        // Folder should be initialized already.

        var item = ViewModel.MailCollection.Find(message.MailUniqueId);

        if (item == null) return;

        await DispatcherQueue.EnqueueAsync(async () =>
        {
            var collectionContainer = await MailListView.GetItemContainersAsync(item);

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

            await WinoClickItemInternalAsync(item, true);
        });
    }

    /// <summary>
    /// Thread header is mail info display control and it can be dragged spearately out of ListView.
    /// We need to prepare a drag package for it from the items inside.
    /// </summary>
    private void ThreadHeaderDragStart(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is MailItemDisplayInformationControl control && control.ActionItem is ThreadMailItemViewModel threadItem)
        {
            args.AllowedOperations = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;

            // Dragging a thread header should move all mails in that thread.
            var draggedThreadItems = threadItem.ThreadEmails.Cast<IMailListItem>().ToList();
            var dragCount = draggedThreadItems.Count;
            var draggingText = string.Format(Translator.MailsDragging, dragCount);

            ViewModel.SetDragState(true, dragCount);

            var dragPackage = new MailDragPackage(draggedThreadItems);

            args.Data.Properties.Add(nameof(MailDragPackage), dragPackage);
            args.Data.SetText(draggingText);
            args.Data.Properties.Title = draggingText;
            args.DragUI.SetContentFromDataPackage();
        }
    }

    private void ThreadHeaderDragFinished(UIElement sender, DropCompletedEventArgs args)
    {
        ViewModel.SetDragState(false);
    }

    private void MailListViewMailDragStateChanged(object? sender, MailDragStateChangedEventArgs e)
    {
        ViewModel.SetDragState(e.IsDragging, e.DraggedItemCount);
    }

    private async void ThreadHeaderTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not MailItemDisplayInformationControl control) return;

        // Hover action button clicks bubble a tap as well; skip selecting in that case.
        if (control.IsRunningHoverAction)
        {
            control.IsRunningHoverAction = false;
            return;
        }

        if (control.ActionItem is ThreadMailItemViewModel threadItem)
        {
            await WinoClickItemInternalAsync(threadItem);
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

    public async Task OnTitleBarSearchTextChangedAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
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
        else if (args.Key == VirtualKey.Escape)
        {
            // Unselect the selected items.
            await ViewModel.MailCollection.UnselectAllAsync();
        }
        else
        {
            args.Handled = false;
        }
    }

    private async Task WinoClickItemInternalAsync(object? clickedItem, bool selectExpandThread = false)
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

        // Treat toolbar multi-select mode the same as holding CTRL for click selection behavior.
        bool isCtrlPressed = KeyPressService.IsCtrlKeyPressed() || ViewModel.IsMultiSelectionModeEnabled;

        // Lazily built caches for this invocation.
        List<ThreadMailItemViewModel>? threadItems = null;

        List<ThreadMailItemViewModel> GetThreadItems()
        {
            return threadItems ??= ViewModel.MailCollection.GetThreadItems();
        }

        ThreadMailItemViewModel? FindParentThread(MailItemViewModel mail) => ViewModel.MailCollection.GetThreadByMailUniqueId(mail.MailCopy.UniqueId);

        void CollapseAllThreadsExcept(ThreadMailItemViewModel? except)
        {
            foreach (var thread in GetThreadItems())
            {
                if (!ReferenceEquals(thread, except) && thread.IsThreadExpanded)
                {
                    thread.IsThreadExpanded = false;
                }
            }
        }

        void ResetSelectionState()
        {
            foreach (var group in ViewModel.MailCollection.MailItems)
            {
                foreach (var item in group)
                {
                    if (item is ThreadMailItemViewModel thread)
                    {
                        thread.IsSelected = false;

                        foreach (var child in thread.ThreadEmails)
                        {
                            child.IsSelected = false;
                        }
                    }
                    else if (item is MailItemViewModel mail)
                    {
                        mail.IsSelected = false;
                    }
                }
            }
        }

        static void SyncThreadSelectionFromChildren(ThreadMailItemViewModel? thread)
        {
            if (thread == null) return;

            bool hasSelectedChildren = false;
            foreach (var child in thread.ThreadEmails)
            {
                if (child.IsSelected)
                {
                    hasSelectedChildren = true;
                    break;
                }
            }

            thread.IsSelected = hasSelectedChildren;

            // Keep thread open while it has selected children.
            if (hasSelectedChildren && !thread.IsThreadExpanded)
            {
                thread.IsThreadExpanded = true;
            }
        }

        await ViewModel.MailCollection.ExecuteSelectionBatchAsync(() =>
        {
            if (isCtrlPressed)
            {
                switch (clickedItem)
                {
                    case ThreadMailItemViewModel thread:
                        {
                            bool allSelected = thread.IsSelected && thread.ThreadEmails.All(e => e.IsSelected);
                            if (allSelected)
                            {
                                thread.IsSelected = false;
                                foreach (var child in thread.ThreadEmails)
                                    child.IsSelected = false;
                            }
                            else
                            {
                                thread.IsSelected = true;
                                foreach (var child in thread.ThreadEmails)
                                    child.IsSelected = true;
                                thread.IsThreadExpanded = true;
                            }
                            break;
                        }
                    case MailItemViewModel mail:
                        {
                            mail.IsSelected = !mail.IsSelected;
                            SyncThreadSelectionFromChildren(FindParentThread(mail));
                            break;
                        }
                }

                return;
            }

            if (clickedItem is ThreadMailItemViewModel clickedThread)
            {
                bool wasThreadSelected = clickedThread.IsSelected;
                bool wasThreadExpanded = clickedThread.IsThreadExpanded;
                var alreadySelectedChild = clickedThread.ThreadEmails.FirstOrDefault(e => e.IsSelected);

                ResetSelectionState();
                CollapseAllThreadsExcept(clickedThread);

                if (wasThreadSelected && wasThreadExpanded)
                {
                    clickedThread.IsThreadExpanded = false;
                    return;
                }

                clickedThread.IsSelected = true;

                if (alreadySelectedChild != null)
                {
                    alreadySelectedChild.IsSelected = true;
                }
                else
                {
                    var defaultSelectedChild = clickedThread.GetDefaultSelectedThreadEmail();
                    if (defaultSelectedChild != null)
                    {
                        defaultSelectedChild.IsSelected = true;
                    }
                }

                clickedThread.IsThreadExpanded = true;
            }
            else if (clickedItem is MailItemViewModel clickedMail)
            {
                bool wasSelected = clickedMail.IsSelected;
                ThreadMailItemViewModel? parentThread = FindParentThread(clickedMail);
                bool isInSelectedExpandedThread = parentThread != null && parentThread.IsSelected && parentThread.IsThreadExpanded;

                if (isInSelectedExpandedThread)
                {
                    var selectedParentThread = parentThread!;

                    foreach (var child in selectedParentThread.ThreadEmails)
                    {
                        child.IsSelected = child == clickedMail && !wasSelected;
                    }

                    SyncThreadSelectionFromChildren(selectedParentThread);
                    return;
                }

                ResetSelectionState();

                if (parentThread != null && parentThread.IsThreadExpanded)
                {
                    CollapseAllThreadsExcept(parentThread);
                }
                else
                {
                    CollapseAllThreadsExcept(null);
                }

                if (parentThread != null && selectExpandThread)
                {
                    parentThread.IsSelected = true;
                    parentThread.IsThreadExpanded = true;
                }

                if (!wasSelected)
                {
                    clickedMail.IsSelected = true;
                }

                SyncThreadSelectionFromChildren(parentThread);
            }
        });
    }

    private async void WinoListViewItemClicked(object sender, ItemClickEventArgs e)
    {
        if (sender is not WinoListView listView) return;

        await WinoClickItemInternalAsync(e.ClickedItem);
    }

    public void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion)
    {
    }

    public Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion)
    {
        SearchText = queryText;

        if (ViewModel.PerformSearchCommand.CanExecute(null))
        {
            ViewModel.PerformSearchCommand.Execute(null);
        }

        return Task.CompletedTask;
    }

    public bool CanPopOutCurrentContent()
    {
        return RenderingFrame.Content is FrameworkElement
               && RenderingFrame.Content is IPopoutClient client
               && client.SupportsPopOut;
    }

    public FrameworkElement? GetCurrentHostedContent()
    {
        return RenderingFrame.Content as FrameworkElement;
    }

    public HostedPopoutDescriptor CreatePopoutDescriptor(IPopoutClient client)
    {
        return client.GetPopoutDescriptor();
    }

    public FrameworkElement DetachHostedContent()
    {
        if (RenderingFrame.Content is not FrameworkElement content)
            throw new InvalidOperationException("RenderingFrame does not host detachable content.");

        InvalidatePendingIdleNavigation();
        DetachPopoutClient();
        RenderingFrame.Content = null;
        ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame, NavigationTransitionType.None);

        return content;
    }

    public void OnHostedContentPoppedOut(FrameworkElement content, HostedContentPopoutWindow window, HostedPopoutDescriptor descriptor)
    {
        if (content is IPopoutClient client)
        {
            client.HostActionRequested -= ActivePopoutClient_HostActionRequested;
            client.HostActionRequested += ActivePopoutClient_HostActionRequested;
        }

        _hostedPopoutWindows[content] = window;
        _ = ViewModel.MailCollection.UnselectAllAsync();
        UpdateAdaptiveness();
    }

    public void OnHostedPopoutClosed(FrameworkElement content, HostedPopoutDescriptor descriptor)
    {
        if (_hostedPopoutWindows.Remove(content) && content is IPopoutClient hostedClient)
        {
            hostedClient.HostActionRequested -= ActivePopoutClient_HostActionRequested;
        }

        if (_pendingHostedPopoutNavigation?.SourceContent == content)
        {
            _pendingHostedPopoutNavigation = null;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (content is IPopoutClient client)
            {
                client.OnPopoutStateChanged(false);
            }

            WindowCleanupHelper.CleanupObject(content);
        });
    }

    private void HandleHostedClientAction(FrameworkElement content, PopoutHostActionRequestedEventArgs args)
    {
        if (!_hostedPopoutWindows.TryGetValue(content, out var hostedWindow))
            return;

        switch (args.ActionKind)
        {
            case PopoutHostActionKind.CloseHostedInstance:
                hostedWindow.Close();
                break;
            case PopoutHostActionKind.PopOutNextNavigation when args.TargetPageType != null:
                _pendingHostedPopoutNavigation = new PendingHostedPopoutNavigation(content, hostedWindow, args.TargetPageType, args.TargetMailUniqueId);
                break;
        }
    }

    private static bool TryGetPendingHostedPopoutTarget(object? currentContent, PendingHostedPopoutNavigation pendingHostedNavigation, out FrameworkElement hostedContent)
    {
        hostedContent = null!;

        if (currentContent is not FrameworkElement currentFrameworkElement || currentFrameworkElement.GetType() != pendingHostedNavigation.TargetPageType)
            return false;

        if (pendingHostedNavigation.TargetMailUniqueId.HasValue
            && currentFrameworkElement is ComposePage composePage
            && composePage.ViewModel.CurrentMailDraftItem?.MailCopy?.UniqueId != pendingHostedNavigation.TargetMailUniqueId.Value)
        {
            return false;
        }

        hostedContent = currentFrameworkElement;
        return true;
    }

    private async Task ContinuePendingHostedPopoutNavigationAsync(FrameworkElement content, PendingHostedPopoutNavigation pendingHostedNavigation)
    {
        if (!ReferenceEquals(_pendingHostedPopoutNavigation, pendingHostedNavigation))
            return;

        _pendingHostedPopoutNavigation = null;

        var didPopOut = await HostedContentPopoutCoordinator.PopOutCurrentContentAsync(this);

        if (didPopOut)
        {
            pendingHostedNavigation.SourceWindow.Close();
        }
    }

    private sealed record PendingHostedPopoutNavigation(
        FrameworkElement SourceContent,
        HostedContentPopoutWindow SourceWindow,
        Type TargetPageType,
        Guid? TargetMailUniqueId);
}
