using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using MoreLinq;
using Serilog;
using Windows.Foundation;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Controls.ListView;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Mail.WinUI.Services;
using Wino.Mail.WinUI.Views;
using Wino.MenuFlyouts.Context;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;
using Wino.Views.Abstract;

namespace Wino.Views.Mail;

public sealed partial class MailListPage : MailListPageAbstract,
    IRecipient<ClearMailSelectionsRequested>,
    IRecipient<ActiveMailItemChangedEvent>,
    IRecipient<SelectMailItemContainerEvent>,
    IRecipient<ComposeDetachedDraftRequested>,
    IRecipient<DisposeRenderingFrameRequested>,
    IRecipient<WinoIntelligenceAccessChanged>,
    IHostedPopoutSource,
    IWinoFrameProvider,
    IMailTitleBarSearchHost
{
    public event EventHandler<bool> SemanticSearchBusyChanged;
    private const double RENDERING_COLUMN_MIN_WIDTH = 375;
    private const int SELECTION_SETTLE_DELAY_MS = 120;
    private const int RENDERING_FRAME_RELEASE_DELAY_MS = 2000;
    private const int SELECT_MAIL_CONTAINER_MAX_ATTEMPTS = 40;
    private const int SELECT_MAIL_CONTAINER_RETRY_DELAY_MS = 50;
    private int _idleNavigationRequestVersion = 0;
    private int _mailActivationRequestVersion = 0;
    private int _selectMailContainerRequestVersion = 0;

    /// <summary>
    /// True while the rendering frame hosts a composer for a draft that is not part of the
    /// mail listing. The reader panel has to stay visible even though nothing is selected.
    /// </summary>
    private bool _isDetachedComposerActive;

    private IPopoutClient? _activePopoutClient;
    private readonly Dictionary<FrameworkElement, HostedContentPopoutWindow> _hostedPopoutWindows = [];
    private PendingHostedPopoutNavigation? _pendingHostedPopoutNavigation;
    private CollectionViewSource MailCollectionViewSource =>
        (CollectionViewSource)Resources["MailCollectionViewSource"];

    private IContactService ContactService { get; } = WinoApplication.Current.Services.GetRequiredService<IContactService>();
    private IFolderService FolderService { get; } = WinoApplication.Current.Services.GetRequiredService<IFolderService>();
    private IAccountService AccountService { get; } = WinoApplication.Current.Services.GetRequiredService<IAccountService>();
    private IIntelligenceSearchEligibilityService IntelligenceEligibilityService { get; } = WinoApplication.Current.Services.GetRequiredService<IIntelligenceSearchEligibilityService>();
    private IMailDialogService MailDialogService { get; } = WinoApplication.Current.Services.GetRequiredService<IMailDialogService>();

    private IStatePersistanceService StatePersistenceService { get; } = WinoApplication.Current.Services.GetService<IStatePersistanceService>() ?? throw new Exception($"Can't resolve {nameof(IStatePersistanceService)}");
    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
    public SearchBarMode SearchMode => SearchBarMode.Mail;
    public IReadOnlyList<SearchBarContactSuggestion> SenderSuggestions { get; private set; } = [];
    public bool IsSemanticSearchAvailable => ViewModel.IsSemanticSearchAvailable;
    public bool IsSemanticSearchBusy => ViewModel.IsSemanticSearchBusy;
    public string SemanticUnavailableReasonText => IsSemanticSearchAvailable ? string.Empty : Translator.WinoIntelligence_InsightsLocked;
    public IReadOnlyList<SearchBarOptionItem> ScopeOptions
    {
        get
        {
            var options = new List<SearchBarOptionItem>
            {
                new((int)SearchBarScope.CurrentFolder, Translator.SearchBar_ScopeCurrentFolder),
            };
            if (ViewModel.ActiveFolder?.HandlingFolders.Select(folder => folder.MailAccountId).Distinct().Take(2).Count() == 1)
                options.Add(new((int)SearchBarScope.CurrentAccount, Translator.SearchBar_ScopeCurrentAccount));
            options.Add(new((int)SearchBarScope.AllAccounts, Translator.SearchBar_ScopeAllAccounts));
            return options;
        }
    }
    public string SearchText
    {
        get => ViewModel.SearchQuery;
        set => ViewModel.SearchQuery = value;
    }

    public string SearchPlaceholderText => Translator.SearchBarPlaceholder;
    public MailListPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ViewModel.IsSemanticSearchBusy))
                SemanticSearchBusyChanged?.Invoke(this, ViewModel.IsSemanticSearchBusy);
        };
        MailListView.GroupedViewSource = MailCollectionViewSource;
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

        MailListView.MailDragStateChanged += MailListViewMailDragStateChanged;

        UpdateSelectAllButtonStatus();
        UpdateAdaptiveness();

        // Delegate to ViewModel.
        if (e.Parameter is NavigateMailFolderEventArgs folderNavigationArgs)
        {
            WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folderNavigationArgs.BaseFolderMenuItem, folderNavigationArgs.FolderInitLoadAwaitTask));
        }
    }

    private void MailListPageLoaded(object sender, RoutedEventArgs e)
    {
        MailGroupNavigator.ItemsSource = MailCollectionViewSource.View?.CollectionGroups;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        _isDetachedComposerActive = false;

        InvalidatePendingIdleNavigation();
        InvalidatePendingMailActivation();
        InvalidatePendingMailContainerSelection();
        DetachPopoutClient();

        this.Bindings.StopTracking();

        MailListView.MailDragStateChanged -= MailListViewMailDragStateChanged;
        SelectAllCheckbox.Checked -= SelectAllCheckboxChecked;
        SelectAllCheckbox.Unchecked -= SelectAllCheckboxUnchecked;
        ViewModel.SetDragState(false);

        MailListView.Cleanup();

        RenderingFrame.Navigate(typeof(IdlePage));
    }

    private void UpdateSelectAllButtonStatus()
    {
        // Check all checkbox if all is selected.
        // Unhook events to prevent selection overriding.

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            SelectAllCheckbox.Checked -= SelectAllCheckboxChecked;
            SelectAllCheckbox.Unchecked -= SelectAllCheckboxUnchecked;

            SelectAllCheckbox.IsChecked = ViewModel.IsAllItemsSelected;

            SelectAllCheckbox.Checked += SelectAllCheckboxChecked;
            SelectAllCheckbox.Unchecked += SelectAllCheckboxUnchecked;
        });
    }

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

        MailListView.ClearSelection();
        await MailListView.WaitForSelectionSyncAsync();

        UpdateSelectAllButtonStatus();
        ViewModel.SelectedPivotChangedCommand.Execute(ViewModel.SelectedFolderPivot);
    }

    private async void SelectAllCheckboxChecked(object sender, RoutedEventArgs e)
    {
        MailListView.SelectAll();
        await MailListView.WaitForSelectionSyncAsync();
    }

    private async void SelectAllCheckboxUnchecked(object sender, RoutedEventArgs e)
    {
        MailListView.ClearSelection();
        await MailListView.WaitForSelectionSyncAsync();
    }

    private async void MailItemContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // Context is requested from a single mail point, but we might have multiple selected items.
        // If the clicked mail is already selected, keep calculating against all selected mails.
        // Otherwise, target only the clicked mail/thread without activating it in the reader.

        if (sender is FrameworkElement control && args.TryGetPosition(sender, out Point p))
        {
            IReadOnlyList<MailItemViewModel> targetItems;
            MailItemViewModel? composeTargetItem;
            var row = ResolveMailListRow(control);
            var actionItem = row?.SourceItem as IMailListItem ?? ResolveMailListItem(control);

            if (row is { IsThreadHead: true })
            {
                targetItems = row.LeafItems.OfType<MailItemViewModel>().ToArray();
                composeTargetItem = row.SourceItem as MailItemViewModel;
            }
            else if (actionItem is MailItemViewModel mailItem)
            {
                targetItems = ViewModel.IsMailSelected(mailItem.UniqueId)
                    ? ViewModel.SelectedItems.ToList()
                    : [mailItem];
                composeTargetItem = mailItem;
            }
            else
            {
                return;
            }

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

#if DEBUG
            if (clickedAction.CreateTestNotification)
            {
                await ViewModel.CreateTestNotificationsAsync(targetItems);
                return;
            }
#endif

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

            var operation = clickedAction.Operation.Operation;

            if (IsComposeContextOperation(operation))
            {
                await ViewModel.CreateDraftFromMailAsync(composeTargetItem, operation);
                return;
            }

            if (operation == MailOperation.RetryDraftUpload)
            {
                await ViewModel.RetryDraftUploadAsync(composeTargetItem);
                return;
            }

            var prepRequest = new MailOperationPreperationRequest(operation, targetItems.Select(a => a.MailCopy));

            await ViewModel.ExecuteMailOperationAsync(prepRequest);
        }
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

            AddMailOperationFlyoutItem(flyout, source, action);
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
                Text = Translator.MailCategoryMenuItem,
                Icon = new SymbolIcon(Symbol.Tag)
            };

            var favoriteCategories = availableCategories.Where(category => category.IsFavorite).ToList();
            var remainingCategories = availableCategories.Where(category => !category.IsFavorite).ToList();

            foreach (var category in favoriteCategories)
            {
                AddCategoryFlyoutItem(categorySubItem, category, assignedCategoryIds, source, flyout);
            }

            if (favoriteCategories.Count > 0 && remainingCategories.Count > 0)
            {
                categorySubItem.Items.Add(new MenuFlyoutSeparator());
            }

            foreach (var category in remainingCategories)
            {
                AddCategoryFlyoutItem(categorySubItem, category, assignedCategoryIds, source, flyout);
            }

            flyout.Items.Add(categorySubItem);
        }

#if DEBUG
        if (flyout.Items.LastOrDefault() is not MenuFlyoutSeparator)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        var testNotificationItem = new MenuFlyoutItem
        {
            Text = Translator.Buttons_TestNotification
        };

        MenuFlyoutLanguageHelper.Apply(testNotificationItem);

        testNotificationItem.Click += (_, _) =>
        {
            source.TrySetResult(new MailContextAction(CreateTestNotification: true));
            flyout.Hide();
        };

        flyout.Items.Add(testNotificationItem);
#endif

        flyout.Closing += (_, _) => source.TrySetResult(null);

        flyout.ShowAt(showAtElement, new FlyoutShowOptions()
        {
            ShowMode = FlyoutShowMode.Standard,
            Position = new Point(x + 30, y - 20)
        });

        return await source.Task;
    }

    private static void AddMailOperationFlyoutItem(
        MenuFlyout flyout,
        TaskCompletionSource<MailContextAction?> source,
        MailOperationMenuItem action)
    {
        var menuFlyoutItem = new MailOperationMenuFlyoutItem(action, clicked =>
        {
            source.TrySetResult(new MailContextAction(clicked));
            flyout.Hide();
        });

        flyout.Items.Add(menuFlyoutItem);
    }

    private static bool IsComposeContextOperation(MailOperation operation)
        => operation is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;

    private static void AddCategoryFlyoutItem(
        MenuFlyoutSubItem categorySubItem,
        MailCategory category,
        IReadOnlyCollection<Guid> assignedCategoryIds,
        TaskCompletionSource<MailContextAction?> source,
        MenuFlyout flyout)
    {
        var wasAssignedToAll = assignedCategoryIds.Contains(category.Id);
        var categoryItem = new ToggleMenuFlyoutItem
        {
            Text = category.Name,
            IsChecked = wasAssignedToAll,
            Icon = new SymbolIcon(Symbol.Tag)
            {
                Foreground = XamlHelpers.GetSolidColorBrushFromHex(category.TextColorHex)
            }
        };

        categoryItem.Click += (_, _) =>
        {
            source.TrySetResult(new MailContextAction(category, wasAssignedToAll));
            flyout.Hide();
        };

        categorySubItem.Items.Add(categoryItem);
    }

    private sealed record MailContextAction(MailOperationMenuItem? Operation = null, MailCategory? Category = null, bool IsCategoryAssignedToAll = false, bool? PinState = null, bool CreateTestNotification = false)
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
        MailListView.ClearSelection();
        await MailListView.WaitForSelectionSyncAsync();
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
        // Selection drives the rendering frame from here on. Any detached composer is replaced.
        _isDetachedComposerActive = false;

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

    private bool IsRenderingPageActive() => RenderingFrame.Content is MailRenderingPage || RenderingFrame.Content is TestPage;
    private bool IsComposingPageActive() => RenderingFrame.Content is ComposePage;

    public Frame? GetFrame(NavigationReferenceFrame frameType)
        => frameType == NavigationReferenceFrame.RenderingFrame ? RenderingFrame : null;

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
        if (ViewModel.SelectedItemsCount != 0) return;
        if (_isDetachedComposerActive) return;

        if (IsRenderingPageActive())
        {
            if (RenderingFrame.Content is MailRenderingPage renderingPage)
            {
                await renderingPage.PrepareForIdleAsync();
            }
        }

        await Task.Delay(RENDERING_FRAME_RELEASE_DELAY_MS);

        if (requestVersion != _idleNavigationRequestVersion) return;
        if (ViewModel.SelectedItemsCount != 0) return;
        if (_isDetachedComposerActive) return;

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

    public void Receive(SelectMailItemContainerEvent message)
    {
        if (message.MailUniqueId == Guid.Empty) return;

        var requestVersion = Interlocked.Increment(ref _selectMailContainerRequestVersion);

        _ = SelectMailItemContainerWhenReadyAsync(message, requestVersion);
    }

    private async Task SelectMailItemContainerWhenReadyAsync(SelectMailItemContainerEvent message, int requestVersion)
    {
        try
        {
            if (!await ViewModel.WaitForCurrentFolderInitializationAsync() ||
                !IsPendingMailContainerSelectionCurrent(requestVersion))
            {
                return;
            }

            for (var attempt = 0; attempt < SELECT_MAIL_CONTAINER_MAX_ATTEMPTS; attempt++)
            {
                if (!IsPendingMailContainerSelectionCurrent(requestVersion)) return;

                var shouldRetry = false;

                await DispatcherQueue.EnqueueAsync(async () =>
                {
                    if (!IsPendingMailContainerSelectionCurrent(requestVersion)) return;

                    if (ViewModel.MailCollection.Find(message.MailUniqueId) == null)
                    {
                        shouldRetry = true;
                        return;
                    }

                    await MailListView.SelectMailAsync(message.MailUniqueId, message.ScrollToItem);
                });

                if (!shouldRetry) return;

                await Task.Delay(SELECT_MAIL_CONTAINER_RETRY_DELAY_MS);
            }

            Log.Warning("Mail item container selection target was not found after retries. MailUniqueId: {MailUniqueId}", message.MailUniqueId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to select mail item container. MailUniqueId: {MailUniqueId}", message.MailUniqueId);
        }
    }

    async void IRecipient<ComposeDetachedDraftRequested>.Receive(ComposeDetachedDraftRequested message)
    {
        if (message.Draft == null) return;

        // The draft is not listed, so there is nothing to select. Drop the current selection
        // and host the composer in the rendering frame on its own.
        InvalidatePendingMailContainerSelection();

        MailListView.ClearSelection();
        await MailListView.WaitForSelectionSyncAsync();

        // Clearing the selection reports a null active mail item, which would navigate the
        // rendering frame to the idle page. Discard that pending work before opening the composer.
        InvalidatePendingMailActivation();
        InvalidatePendingIdleNavigation();

        var composerPageTransition = NavigationTransitionType.None;

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
        else if (!IsComposingPageActive())
        {
            composerPageTransition = NavigationTransitionType.DrillIn;
        }

        _isDetachedComposerActive = true;

        ViewModel.NavigationService.Navigate(WinoPage.ComposePage, message.Draft, NavigationReferenceFrame.RenderingFrame, composerPageTransition);

        UpdateAdaptiveness();
    }

    private bool IsPendingMailContainerSelectionCurrent(int requestVersion)
        => Volatile.Read(ref _selectMailContainerRequestVersion) == requestVersion;

    private void InvalidatePendingMailContainerSelection()
        => Interlocked.Increment(ref _selectMailContainerRequestVersion);

    private void MailListViewMailDragStateChanged(object? sender, MailDragStateChangedEventArgs e)
    {
        ViewModel.SetDragState(e.IsDragging, e.DraggedItemCount);
    }

    public async Task OnTitleBarSearchTextChangedAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            ViewModel.IsOnlineSearchButtonVisible = false;
            ViewModel.SetSearchCriteria(MailSearchCriteria.Empty, []);
            await ViewModel.PerformSearchAsync();
        }
    }

    public void Receive(DisposeRenderingFrameRequested message)
    {
        _isDetachedComposerActive = false;

        InvalidatePendingMailContainerSelection();
        ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame, NavigationTransitionType.DrillIn);
        UpdateAdaptiveness();
    }

    public void Receive(WinoIntelligenceAccessChanged message)
        => DispatcherQueue.TryEnqueue(Bindings.Update);

    protected override void RegisterRecipients()
    {
        WeakReferenceMessenger.Default.Register<ClearMailSelectionsRequested>(this);
        WeakReferenceMessenger.Default.Register<ActiveMailItemChangedEvent>(this);
        WeakReferenceMessenger.Default.Register<SelectMailItemContainerEvent>(this);
        WeakReferenceMessenger.Default.Register<ComposeDetachedDraftRequested>(this);
        WeakReferenceMessenger.Default.Register<DisposeRenderingFrameRequested>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceAccessChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        WeakReferenceMessenger.Default.Unregister<ClearMailSelectionsRequested>(this);
        WeakReferenceMessenger.Default.Unregister<ActiveMailItemChangedEvent>(this);
        WeakReferenceMessenger.Default.Unregister<SelectMailItemContainerEvent>(this);
        WeakReferenceMessenger.Default.Unregister<ComposeDetachedDraftRequested>(this);
        WeakReferenceMessenger.Default.Unregister<DisposeRenderingFrameRequested>(this);
        WeakReferenceMessenger.Default.Unregister<WinoIntelligenceAccessChanged>(this);
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
        bool hasReaderSelection =
            _isDetachedComposerActive ||
            (ViewModel.HasSingleItemSelected && !isMultiSelectionEnabled) ||
            ViewModel.HasSingleFullySelectedThread;

        if (StatePersistenceService.IsReaderNarrowed)
        {
            if (hasReaderSelection)
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
            if (hasReaderSelection)
            {
                VisualStateManager.GoToState(this, "BothPanelsMailSelected", true);
            }
            else
            {
                VisualStateManager.GoToState(this, "BothPanelsNoMailSelected", true);
            }
        }
    }



    private void MailListViewSelectionSnapshotChanged(
        object? sender,
        global::Wino.Mail.Controls.Core.MailListSelectionSnapshot snapshot)
    {
        ViewModel.ApplyMailSelectionSnapshot(snapshot);
        UpdateSelectAllButtonStatus();
        UpdateAdaptiveness();
    }


    private async void WinoListViewProcessKeyboardAccelerators(UIElement sender, ProcessKeyboardAcceleratorEventArgs args)
    {
        args.Handled = true;

        if (args.Key == VirtualKey.Z && args.Modifiers.HasFlag(VirtualKeyModifiers.Control))
        {
            await ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null);
        }
        else if (args.Key == VirtualKey.Delete)
        {
            ViewModel.ExecuteMailOperationCommand.Execute(MailOperation.SoftDelete);
        }
        else if (args.Key == VirtualKey.Escape)
        {
            MailListView.ClearSelection();
            await MailListView.WaitForSelectionSyncAsync();
        }
        else
        {
            args.Handled = false;
        }
    }

    private async void UndoKeyboardAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        await ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null);
        args.Handled = true;
    }

    public async Task ClearMailSelectionAsync()
    {
        MailListView.ClearSelection();
        await MailListView.WaitForSelectionSyncAsync();
    }

    private static IMailListItem? ResolveMailListItem(FrameworkElement element)
        => element.DataContext switch
        {
            global::Wino.Mail.Controls.Core.MailListRow row => row.SourceItem as IMailListItem,
            IMailListItem item => item,
            _ => null,
        };

    private static global::Wino.Mail.Controls.Core.MailListRow? ResolveMailListRow(
        DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is global::Wino.Mail.Controls.MailListView.WinoMailListViewItem { Row: { } row })
            {
                return row;
            }

            if (current is FrameworkElement
                {
                    DataContext: global::Wino.Mail.Controls.Core.MailListRow dataRow
                })
            {
                return dataRow;
            }
        }

        return null;
    }

    private void MailRowPointerEntered(object sender, PointerRoutedEventArgs e)
        => SetMailRowHoverActionVisibility(sender as DependencyObject, true);

    private void MailRowPointerExited(object sender, PointerRoutedEventArgs e)
        => SetMailRowHoverActionVisibility(sender as DependencyObject, false);

    private void ThreadExpanderPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Mail & Calendar treats every row surface as a selection target while touch
        // multi-select is active; the chevron must not change thread structure.
        if (MailListView.IsTouchMultiSelectMode)
        {
            return;
        }

        switch ((sender as FrameworkElement)?.DataContext)
        {
            case global::Wino.Mail.Controls.Core.MailListRow
            {
                Kind: global::Wino.Mail.Controls.Core.MailListRowKind.ThreadHead
            } row:
                if (row.IsExpanded)
                {
                    MailListView.CollapseThreadFromExpander(row.ThreadKey);
                }
                else
                {
                    MailListView.ExpandThreadFromExpander(row.ThreadKey);
                }

                e.Handled = true;
                break;
        }
    }

    private void SetMailRowHoverActionVisibility(DependencyObject? rowRoot, bool isVisible)
    {
        var hoverActionButtons = FindDescendantByName<FrameworkElement>(rowRoot, "HoverActionButtons");

        if (hoverActionButtons == null)
            return;

        var shouldShowHoverActions = isVisible && ViewModel.PreferencesService.IsHoverActionsEnabled;

        hoverActionButtons.Visibility = shouldShowHoverActions
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (shouldShowHoverActions)
        {
            RefreshHoverActionButtons(hoverActionButtons);
        }

        SetRightAccountNicknameIndicatorVisibility(rowRoot, !shouldShowHoverActions);
    }

    private static void SetRightAccountNicknameIndicatorVisibility(DependencyObject? rowRoot, bool isVisible)
    {
        foreach (var indicator in FindDescendants<FrameworkElement>(rowRoot))
        {
            if (indicator.Name is "RightAccountNicknameIndicator" or "RightNicknameIndicator")
            {
                indicator.Visibility = isVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private static void RefreshHoverActionButtons(DependencyObject hoverActionButtons)
    {
        foreach (var button in FindDescendants<Button>(hoverActionButtons))
        {
            RefreshHoverActionButton(button);
        }
    }

    private static void RefreshHoverActionButton(Button button)
    {
        if (button.Tag is not string actionIndexText || !int.TryParse(actionIndexText, out var actionIndex))
            return;

        AutomationProperties.SetName(button, XamlHelpers.GetHoverActionOperationString(actionIndex));

        if (button.Content is WinoFontIcon icon)
        {
            icon.Icon = XamlHelpers.GetHoverActionWinoIconGlyph(actionIndex);
        }
    }

    private void MailRowHoverActionButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            RefreshHoverActionButton(button);
        }
    }

    private static T? FindDescendantByName<T>(DependencyObject? rootElement, string name)
        where T : FrameworkElement
    {
        if (rootElement == null)
            return null;

        var childrenCount = VisualTreeHelper.GetChildrenCount(rootElement);

        for (var i = 0; i < childrenCount; i++)
        {
            var child = VisualTreeHelper.GetChild(rootElement, i);

            if (child is T frameworkElement && frameworkElement.Name == name)
                return frameworkElement;

            var descendant = FindDescendantByName<T>(child, name);

            if (descendant != null)
                return descendant;
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject? rootElement)
        where T : DependencyObject
    {
        if (rootElement == null)
            yield break;

        var childrenCount = VisualTreeHelper.GetChildrenCount(rootElement);

        for (var i = 0; i < childrenCount; i++)
        {
            var child = VisualTreeHelper.GetChild(rootElement, i);

            if (child is T match)
                yield return match;

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void MailRowHoverActionTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: string actionIndexText } element ||
            !int.TryParse(actionIndexText, out var actionIndex))
        {
            return;
        }

        var operation = XamlHelpers.GetHoverAction(actionIndex);
        var row = ResolveMailListRow(element);
        var targetItems = row?.LeafItems.OfType<MailItemViewModel>().ToArray() ??
            (element.DataContext is MailItemViewModel mailItem
                ? [mailItem]
                : []);
        ExecuteHoverAction(targetItems, row?.SourceItem as MailItemViewModel, operation);
    }

    private async void ExecuteHoverAction(
        IReadOnlyList<MailItemViewModel> targetItems,
        MailItemViewModel? representativeItem,
        MailOperation operation)
    {
        if (targetItems.Count == 0)
        {
            return;
        }

        if (IsComposeContextOperation(operation))
        {
            var composeTargetItem = representativeItem ?? targetItems[0];

            await ViewModel.CreateDraftFromMailAsync(composeTargetItem, operation);

            return;
        }

        var package = targetItems.Count == 1
            ? new MailOperationPreperationRequest(operation, targetItems[0].MailCopy, toggleExecution: true)
            : new MailOperationPreperationRequest(
                operation,
                targetItems.Select(static item => item.MailCopy),
                toggleExecution: true);
        ViewModel.ExecuteHoverActionCommand.Execute(package);
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

    public async Task RequestSenderSuggestionsAsync(string query)
    {
        var contacts = await ContactService.SearchContactsAsync(query).ConfigureAwait(false);
        var suggestions = contacts.Take(8).Select(contact => new SearchBarContactSuggestion
        {
            DisplayName = contact.DisplayName,
            Address = contact.Address,
            Initials = GetInitials(contact.DisplayName),
            ContactPicture = XamlHelpers.GetContactPicture(contact, contact.DisplayName, contact.Address),
            Tag = contact,
        }).ToArray();
        await DispatcherQueue.EnqueueAsync(() => SenderSuggestions = suggestions);
    }

    public async Task<SemanticSearchAvailability> GetSemanticSearchAvailabilityAsync(SearchBarFilterSnapshot filters)
    {
        var folders = await ResolveSearchFoldersAsync(filters.Scope).ConfigureAwait(false);
        var eligibility = await IntelligenceEligibilityService.ResolveAsync(
            folders.Select(folder => folder.MailAccountId).Distinct().ToArray()).ConfigureAwait(false);
        if (!eligibility.HasCompatibleBackends)
            return new(false, Translator.SearchBar_SemanticUnavailableMixedBackend);
        if (!eligibility.HasEligibleAccounts)
            return new(false, Translator.WinoIntelligence_InsightsLocked);
        return new(true, string.Empty);
    }

    public async Task OnMailSearchSubmittedAsync(SearchBarSubmittedEventArgs args)
    {
        IReadOnlyList<MailItemFolder> folders = await ResolveSearchFoldersAsync(args.Filters.Scope).ConfigureAwait(false);
        if (args.IsSemanticSearchEnabled)
        {
            var eligibility = await IntelligenceEligibilityService.ResolveAsync(
                folders.Select(folder => folder.MailAccountId).Distinct().ToArray()).ConfigureAwait(false);
            if (!eligibility.HasCompatibleBackends)
            {
                await DispatcherQueue.EnqueueAsync(() => MailDialogService.InfoBarMessage(
                    Translator.GeneralTitle_Error,
                    Translator.SearchBar_SemanticUnavailableMixedBackend,
                    InfoBarMessageType.Warning));
                return;
            }

            var eligibleAccountIds = eligibility.Accounts.Where(account => account.IsEligible).Select(account => account.AccountId).ToHashSet();
            var omittedNames = eligibility.Accounts.Where(account => !account.IsEligible).Select(account => account.AccountName).ToArray();
            folders = folders.Where(folder => eligibleAccountIds.Contains(folder.MailAccountId)).ToArray();
            if (omittedNames.Length > 0)
            {
                await DispatcherQueue.EnqueueAsync(() => MailDialogService.InfoBarMessage(
                    Translator.SemanticSearch_PartialTitle,
                    string.Format(Translator.SemanticSearch_OmittedAccountsMessage, string.Join(", ", omittedNames)),
                    InfoBarMessageType.Warning));
            }
            if (folders.Count == 0)
                return;
        }
        var (afterUtc, beforeUtc) = ResolveUtcDateRange(args.Filters.DateRange, DateTime.Now);
        var executionMode = args.IsSemanticSearchEnabled
            ? Wino.Core.Domain.Enums.SearchMode.Semantic
            : args.Filters.Reach == SearchBarReach.IncludeServer
                ? Wino.Core.Domain.Enums.SearchMode.Online
                : Wino.Core.Domain.Enums.SearchMode.Local;
        var criteria = new MailSearchCriteria(
            args.QueryText.Trim(),
            executionMode,
            (MailSearchScope)(int)args.Filters.Scope,
            (MailSearchReach)(int)args.Filters.Reach,
            args.Filters.Sender.Trim(),
            afterUtc,
            beforeUtc,
            args.Filters.HasAttachments,
            args.Filters.IsUnread,
            args.Filters.IsFlagged,
            folders.Select(folder => folder.Id).ToArray(),
            folders.Select(folder => folder.MailAccountId).Distinct().ToArray());

        await DispatcherQueue.EnqueueAsync(() =>
        {
            ViewModel.SetSearchCriteria(criteria, folders);
            if (ViewModel.PerformSearchCommand.CanExecute(null))
                ViewModel.PerformSearchCommand.Execute(null);
        });
    }

    private async Task<IReadOnlyList<MailItemFolder>> ResolveSearchFoldersAsync(SearchBarScope scope)
    {
        var activeFolders = ViewModel.ActiveFolder?.HandlingFolders
            .OfType<MailItemFolder>()
            .Where(IsRealSearchableFolder)
            .ToArray() ?? [];
        if (scope == SearchBarScope.CurrentFolder)
            return activeFolders;

        var accountIds = scope == SearchBarScope.CurrentAccount
            ? activeFolders.Select(folder => folder.MailAccountId).Distinct().Take(2).ToArray()
            : (await AccountService.GetAccountsAsync().ConfigureAwait(false)).Select(account => account.Id).ToArray();
        if (scope == SearchBarScope.CurrentAccount && accountIds.Length != 1)
            return activeFolders;

        var folders = new List<MailItemFolder>();
        foreach (var accountId in accountIds)
            folders.AddRange(await FolderService.GetFoldersAsync(accountId).ConfigureAwait(false));
        return folders.Where(IsRealSearchableFolder).GroupBy(folder => folder.Id).Select(group => group.First()).ToArray();
    }

    private static bool IsRealSearchableFolder(MailItemFolder folder)
        => folder is not null && !string.IsNullOrWhiteSpace(folder.RemoteFolderId);

    internal static (DateTimeOffset? AfterUtc, DateTimeOffset? BeforeUtc) ResolveUtcDateRange(
        SearchBarDateRange range,
        DateTime localNow)
    {
        if (range == SearchBarDateRange.AnyTime)
            return (null, null);

        var end = localNow.Date.AddDays(1);
        var start = range switch
        {
            SearchBarDateRange.Today => localNow.Date,
            SearchBarDateRange.LastSevenDays => localNow.Date.AddDays(-6),
            SearchBarDateRange.LastThirtyDays => localNow.Date.AddDays(-29),
            _ => localNow.Date,
        };
        return (new DateTimeOffset(start).ToUniversalTime(), new DateTimeOffset(end).ToUniversalTime());
    }

    private static string GetInitials(string value)
    {
        var words = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Take(2).Select(word => char.ToUpperInvariant(word[0])));
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

        _isDetachedComposerActive = false;

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
        MailListView.ClearSelection();
        _ = MailListView.WaitForSelectionSyncAsync();
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

    private void MultiSelectChecked(object sender, RoutedEventArgs e) => MailListView.SelectionMode = ListViewSelectionMode.Multiple;

    private void MultiSelectUnchecked(object sender, RoutedEventArgs e) => MailListView.SelectionMode = ListViewSelectionMode.Extended;

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
