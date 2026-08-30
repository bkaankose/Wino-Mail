using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq;
using Nito.AsyncEx;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Mail;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Mail.Controls.Core;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class MailListPageViewModel : MailBaseViewModel,
    IShellMenuOwner,
    IRecipient<MailItemNavigationRequested>,
    IRecipient<ActiveMailFolderChangedEvent>,
    IRecipient<AccountSynchronizationCompleted>,
    IRecipient<NewMailSynchronizationRequested>,
    IRecipient<AccountSynchronizerStateChanged>,
    IRecipient<AccountCacheResetMessage>,
    IRecipient<ThumbnailAdded>,
    IRecipient<MailOperationRequested>,
    IRecipient<UndoableMailActionPackChanged>,
    IRecipient<IntelligenceMetadataChanged>,
    IRecipient<IntelligenceVisibilityChanged>,
    IRecipient<LanguageChanged>
{
    private Guid? trackingSynchronizationId = null;
    private int completedTrackingSynchronizationCount = 0;

    /* [Bug] Unread folder reads All emails automatically with setting "Mark as Read: When Selected" enabled 
     * https://github.com/bkaankose/Wino-Mail/issues/162
     * We store the UniqueIds of the mails that are marked as read in Gmail Unread folder
     * to prevent them from being removed from the list when they are marked as read.
     */

    private readonly HashSet<Guid> gmailUnreadFolderMarkedAsReadUniqueIds = [];

    public MailListStore MailCollection { get; } = new();

    [ObservableProperty]
    public partial MailListProjectionOptions MailListOptions { get; set; } = new();
    public ObservableCollection<FolderPivotViewModel> PivotFolders { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<IMenuOperation> ActionItems { get; set; } = [];

    private readonly SemaphoreSlim listManipulationSemepahore = new SemaphoreSlim(1);
    private readonly object mailLoadSync = new();
    private CancellationTokenSource mailLoadCancellationTokenSource = new();
    private long mailLoadGeneration;
    private long folderChangeGeneration;
    private int mailNavigationRequestVersion;
    private bool isLoadingMore;
    private MailFetchCursor nextMailCursor;
    private FolderPivotViewModel lastRequestedPivot;
    private TaskCompletionSource<bool> pendingFolderCompletion;

    public INavigationService NavigationService { get; }
    public IStatePersistanceService StatePersistenceService { get; }
    public IPreferencesService PreferencesService { get; }
    public INewThemeService ThemeService { get; }

    private readonly IAccountService _accountService;
    private readonly IMailDialogService _mailDialogService;
    private readonly IMailService _mailService;
    private readonly IMimeFileService _mimeFileService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IFolderService _folderService;
    private readonly IContextMenuItemService _contextMenuItemService;
    private readonly ILogger _logger = Log.ForContext<MailListPageViewModel>();
    private readonly IMailCategoryService _mailCategoryService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;
    private readonly IKeyPressService _keyPressService;
    private readonly IWinoLogger _winoLogger;
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IDraftSyncRetryService _draftSyncRetryService;
    private readonly IIntelligenceSearchService _intelligenceSearchService;
    private MailItemViewModel _activeMailItem;
    private IReadOnlyList<MailItemViewModel> _selectedItems = [];
    private IReadOnlySet<Guid> _selectedItemIds = new HashSet<Guid>();
    private IReadOnlySet<string> _fullySelectedThreadKeys = new HashSet<string>(StringComparer.Ordinal);

    public List<SortingOption> SortingOptions { get; } =
    [
        new(Translator.SortingOption_Date, SortingOptionType.ReceiveDate),
        new(Translator.SortingOption_Name, SortingOptionType.Sender),
    ];

    public List<FilterOption> FilterOptions { get; } =
    [
        new (Translator.FilteringOption_All, FilterOptionType.All),
        new (Translator.FilteringOption_Unread, FilterOptionType.Unread),
        new (Translator.FilteringOption_Flagged, FilterOptionType.Flagged),
        new (Translator.FilteringOption_Files, FilterOptionType.Files)
    ];

    private FolderPivotViewModel _selectedFolderPivot;

    [ObservableProperty]
    public partial bool IsMultiSelectionModeEnabled { get; set; }

    partial void OnIsMultiSelectionModeEnabledChanged(bool value)
    {
        foreach (var pivot in PivotFolders)
        {
            pivot.IsExtendedMode = value;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMessageText))]
    [NotifyPropertyChangedFor(nameof(DraggingMessageText))]
    public partial bool IsDragInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMessageText))]
    [NotifyPropertyChangedFor(nameof(DraggingMessageText))]
    public partial int DraggingItemsCount { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; }

    public MailSearchCriteria SearchCriteria { get; private set; } = MailSearchCriteria.Empty;
    private IReadOnlyList<IMailItemFolder> SearchHandlingFolders { get; set; } = [];

    public bool IsSemanticSearchAvailable => _intelligenceSearchService is not null;

    [ObservableProperty]
    public partial bool IsSemanticSearchBusy { get; set; }

    public void SetSearchCriteria(MailSearchCriteria criteria, IReadOnlyList<IMailItemFolder> folders)
    {
        SearchCriteria = criteria ?? MailSearchCriteria.Empty;
        SearchHandlingFolders = folders ?? [];
        SearchQuery = SearchCriteria.Query;
        IsOnlineSearchEnabled = SearchCriteria.ExecutionMode == SearchMode.Online;
    }

    [ObservableProperty]
    public partial FilterOption SelectedFilterOption { get; set; }
    private SortingOption _selectedSortingOption;

    // Indicates state when folder is initializing. It can happen after folder navigation, search or filter change applied or loading more items.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(IsFolderEmpty))]
    [NotifyPropertyChangedFor(nameof(IsProgressRing))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreItemsCommand))]
    public partial bool IsInitializingFolder { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreItemsCommand))]
    public partial bool FinishedLoading { get; set; } = false;

    public bool CanLoadMoreItems => !IsInitializingFolder && !IsOnlineSearchEnabled && !FinishedLoading;

    [ObservableProperty]
    public partial InfoBarMessageType BarSeverity { get; set; }

    [ObservableProperty]
    public partial string BarMessage { get; set; }

    [ObservableProperty]
    public partial double MailListLength { get; set; } = 420;

    [ObservableProperty]
    public partial double MaxMailListLength { get; set; } = 1200;

    [ObservableProperty]
    public partial string BarTitle { get; set; }

    [ObservableProperty]
    public partial bool IsBarOpen { get; set; }

    [ObservableProperty]
    public partial bool IsUndoMailActionBarOpen { get; set; }

    [ObservableProperty]
    public partial string UndoMailActionBarTitle { get; set; }

    [ObservableProperty]
    public partial InfoBarMessageType UndoMailActionBarSeverity { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoMailActionBarDismissInterval))]
    public partial UndoableMailActionPack CurrentVisibleUndoMailActionPack { get; set; }

    public int UndoMailActionBarDismissInterval => Math.Clamp(CurrentVisibleUndoMailActionPack?.IntervalInSeconds ?? 5, 1, 10);

    /// <summary>
    /// Current folder that is being represented from the menu.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize))]
    [NotifyPropertyChangedFor(nameof(IsFolderSynchronizationEnabled))]
    [NotifyPropertyChangedFor(nameof(IsCategoryView))]
    [NotifyPropertyChangedFor(nameof(IsSyncButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsJunkFolder))]
    [NotifyPropertyChangedFor(nameof(IsEmptyFolderButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsMergedAccountView))]
    [NotifyCanExecuteChangedFor(nameof(EmptyFolderCommand))]
    public partial IBaseFolderMenuItem ActiveFolder { get; set; }

    public bool IsMergedAccountView => ActiveFolder is IMergedAccountFolderMenuItem or IMergedMailCategoryMenuItem;

    private AccountNicknamePosition CurrentAccountNicknamePosition
        => IsMergedAccountView ? PreferencesService.AccountNicknamePosition : AccountNicknamePosition.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize))]
    [NotifyCanExecuteChangedFor(nameof(EmptyFolderCommand))]
    public partial bool IsAccountSynchronizerInSynchronization { get; set; }

    /// <summary>
    /// The mail pane belongs to the mail mode view model. The page hands it to the shell
    /// when the inner frame navigates here.
    /// </summary>
    public IShellMenuProvider ShellMenuProvider { get; }

    public MailListPageViewModel(IMailDialogService dialogService,
                                 INavigationService navigationService,
                                 IAccountService accountService,
                                 IMailDialogService mailDialogService,
                                 IMailService mailService,
                                 IMimeFileService mimeFileService,
                                 IStatePersistanceService statePersistenceService,
                                 INotificationBuilder notificationBuilder,
                                 IFolderService folderService,
                                 IContextMenuItemService contextMenuItemService,
                                 IMailCategoryService mailCategoryService,
                                 IWinoRequestDelegator winoRequestDelegator,
                                 IKeyPressService keyPressService,
                                 IPreferencesService preferencesService,
                                 INewThemeService themeService,
                                 IWinoLogger winoLogger,
                                 ISynchronizationManager synchronizationManager,
                                 IDraftSyncRetryService draftSyncRetryService,
                                 IMailShellClient shellMenuProvider = null,
                                 IIntelligenceSearchService intelligenceSearchService = null)
    {
        ShellMenuProvider = shellMenuProvider;

        _winoLogger = winoLogger;
        _accountService = accountService;
        _mailDialogService = mailDialogService;
        _mailService = mailService;
        _mimeFileService = mimeFileService;
        _folderService = folderService;
        _contextMenuItemService = contextMenuItemService;
        _mailCategoryService = mailCategoryService;
        _winoRequestDelegator = winoRequestDelegator;
        _keyPressService = keyPressService;
        _synchronizationManager = synchronizationManager;
        _draftSyncRetryService = draftSyncRetryService;
        _intelligenceSearchService = intelligenceSearchService;

        PreferencesService = preferencesService;
        ThemeService = themeService;
        StatePersistenceService = statePersistenceService;
        _notificationBuilder = notificationBuilder;
        NavigationService = navigationService;

        SelectedFilterOption = FilterOptions[0];
        SelectedSortingOption = SortingOptions[0];
        MailCollection.MailItemFactory = CreateMailItemViewModel;
        RefreshMailListOptions();

        MailListLength = statePersistenceService.MailListPaneLength;
    }

    partial void OnActiveFolderChanged(IBaseFolderMenuItem value)
    {
        UpdateAccountNicknamePositionForItems();
    }

    private MailItemViewModel CreateMailItemViewModel(MailCopy mailCopy)
        => new(mailCopy, CurrentAccountNicknamePosition);

    private void UpdateAccountNicknamePositionForItems()
        => MailCollection.UpdateAccountNicknamePosition(CurrentAccountNicknamePosition);

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        PreferencesService.PreferenceChanged += PreferencesServiceChanged;
    }

    public override async void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        PreferencesService.PreferenceChanged -= PreferencesServiceChanged;
        CancelActiveMailLoad();
        CompletePendingFolderNavigation(false);

        var pendingTrace = MailListLoadTrace.Current;
        ReportMailLoadTrace(pendingTrace);
        MailListLoadTrace.End(pendingTrace);

        await MailCollection.ClearAsync();
        MailCollection.Cleanup();
    }

    public IReadOnlyList<MailItemViewModel> SelectedItems => _selectedItems;

    public int SelectedItemsCount => _selectedItems.Count;

    public bool HasSingleItemSelected => SelectedItemsCount == 1;

    public bool IsAllItemsSelected =>
        MailCollection.AllItemsCount > 0 &&
        SelectedItemsCount == MailCollection.AllItemsCount;

    public bool HasSingleFullySelectedThread
    {
        get
        {
            if (_fullySelectedThreadKeys.Count != 1 || SelectedItemsCount < 2)
            {
                return false;
            }

            var threadKey = _fullySelectedThreadKeys.First();
            return _selectedItems.All(item =>
                string.Equals(item.ThreadKey, threadKey, StringComparison.Ordinal));
        }
    }

    public bool IsMailSelected(Guid uniqueId) => _selectedItemIds.Contains(uniqueId);

    public Task CreateTestNotificationsAsync(IEnumerable<MailItemViewModel> mailItems)
        => _notificationBuilder.CreateTestNotificationsAsync(mailItems.Select(static item => item.MailCopy));

    public void ApplyMailSelectionSnapshot(MailListSelectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _selectedItems = snapshot.SelectedItems
            .OfType<MailItemViewModel>()
            .DistinctBy(static item => item.UniqueId)
            .ToArray();
        _selectedItemIds = _selectedItems
            .Select(static item => item.UniqueId)
            .ToHashSet();
        _fullySelectedThreadKeys = snapshot.FullySelectedThreadKeys
            .ToHashSet(StringComparer.Ordinal);

        var activeItem = HasSingleItemSelected
            ? _selectedItems[0]
            : HasSingleFullySelectedThread
                ? snapshot.ActiveItem as MailItemViewModel ??
                  GetDefaultThreadItem(_fullySelectedThreadKeys.First())
                : null;
        ActiveMailItemChanged(activeItem);

        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(SelectedItemsCount));
        OnPropertyChanged(nameof(HasSingleItemSelected));
        OnPropertyChanged(nameof(IsAllItemsSelected));
        OnPropertyChanged(nameof(HasSingleFullySelectedThread));
        NotifyItemFoundState();
        NotifyItemSelected();
        SetupTopBarActions();
    }

    private MailItemViewModel GetDefaultThreadItem(string threadKey)
    {
        var threadItems = _selectedItems
            .Where(item => string.Equals(item.ThreadKey, threadKey, StringComparison.Ordinal));

        return PreferencesService.IsNewestThreadMailFirst
            ? threadItems.MaxBy(static item => item.DateSortKey)
            : threadItems.MinBy(static item => item.DateSortKey);
    }

    private void SetupTopBarActions()
    {
        var nextActions = SelectedItemsCount == 0
            ? []
            : GetAvailableMailActions(SelectedItems).Cast<IMenuOperation>().ToArray();

        if (!HaveSameActionState(ActionItems, nextActions))
        {
            ActionItems = nextActions;
        }
    }

    private static bool HaveSameActionState(
        IReadOnlyList<IMenuOperation> current,
        IReadOnlyList<IMenuOperation> next)
    {
        if (ReferenceEquals(current, next))
            return true;

        if (current == null || next == null || current.Count != next.Count)
            return false;

        for (var index = 0; index < current.Count; index++)
        {
            var currentItem = current[index];
            var nextItem = next[index];

            if (currentItem?.GetType() != nextItem?.GetType() ||
                !string.Equals(currentItem?.Identifier, nextItem?.Identifier, StringComparison.Ordinal) ||
                currentItem?.IsEnabled != nextItem?.IsEnabled ||
                currentItem?.IsSecondaryMenuPreferred != nextItem?.IsSecondaryMenuPreferred)
            {
                return false;
            }
        }

        return true;
    }

    #region Properties

    /// <summary>
    /// Selected internal folder. This can be either folder's own name or Focused-Other.
    /// </summary>
    public FolderPivotViewModel SelectedFolderPivot
    {
        get => _selectedFolderPivot;
        set
        {
            if (value == null && ActiveFolder != null)
                return;

            if (_selectedFolderPivot != null)
                _selectedFolderPivot.SelectedItemCount = 0;

            SetProperty(ref _selectedFolderPivot, value);
        }
    }

    /// <summary>
    /// Selected sorting option.
    /// </summary>
    public SortingOption SelectedSortingOption
    {
        get => _selectedSortingOption;
        set
        {
            if (SetProperty(ref _selectedSortingOption, value))
            {
                if (value != null && MailCollection != null)
                {
                    RefreshMailListOptions();
                }
            }
        }
    }

    public bool CanSynchronize => !IsCategoryView && !IsAccountSynchronizerInSynchronization && IsFolderSynchronizationEnabled;
    public bool IsFolderSynchronizationEnabled => ActiveFolder?.IsSynchronizationEnabled ?? false;
    public bool IsArchiveSpecialFolder => ActiveFolder?.SpecialFolderType == SpecialFolderType.Archive;
    public bool IsJunkFolder => ActiveFolder?.SpecialFolderType == SpecialFolderType.Junk;
    public bool IsCategoryView => ActiveFolder is IMailCategoryMenuItem or IMergedMailCategoryMenuItem;
    public bool IsSyncButtonVisible => !IsCategoryView;
    public bool IsEmptyFolderButtonVisible => IsJunkFolder && PreferencesService.IsShowEmptyJunkFolderEnabled;

    public string SelectedMessageText => IsDragInProgress
        ? string.Format(Translator.MailsDragging, DraggingItemsCount)
        : SelectedItemsCount > 0
            ? string.Format(Translator.MailsSelected, SelectedItemsCount)
            : Translator.NoMailSelected;

    public string DraggingMessageText => string.Format(Translator.MailsDragging, DraggingItemsCount);

    /// <summary>
    /// Indicates current state of the mail list. Doesn't matter it's loading or no.
    /// </summary>
    public bool IsEmpty => MailCollection.AllItemsCount == 0;

    /// <summary>
    /// Progress ring only should be visible when the folder is initializing and there are no items. We don't need to show it when there are items.
    /// </summary>
    public bool IsProgressRing => IsInitializingFolder && IsEmpty;
    public bool IsFolderEmpty => !IsInitializingFolder && IsEmpty;

    public bool HasNoOnlineSearchResult { get; private set; }

    [ObservableProperty]
    public partial bool IsInSearchMode { get; set; }

    [ObservableProperty]
    public partial bool IsOnlineSearchButtonVisible { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreItemsCommand))]
    public partial bool IsOnlineSearchEnabled { get; set; }

    [ObservableProperty]
    public partial bool AreSearchResultsOnline { get; set; }

    #endregion

    private async void ActiveMailItemChanged(MailItemViewModel selectedMailItemViewModel)
    {
        if (_activeMailItem == selectedMailItemViewModel) return;

        _activeMailItem = selectedMailItemViewModel;

        Messenger.Send(new ActiveMailItemChangedEvent(_activeMailItem));

        if (_activeMailItem == null || _activeMailItem.IsRead) return;

        // Automatically set mark as read or not based on preferences.

        var markAsPreference = PreferencesService.MarkAsPreference;

        if (markAsPreference == MailMarkAsOption.WhenSelected)
        {
            var operation = MailOperation.MarkAsRead;
            var package = new MailOperationPreperationRequest(operation, _activeMailItem.MailCopy);

            if (ActiveFolder?.SpecialFolderType == SpecialFolderType.Unread &&
                !gmailUnreadFolderMarkedAsReadUniqueIds.Contains(_activeMailItem.UniqueId))
            {
                gmailUnreadFolderMarkedAsReadUniqueIds.Add(_activeMailItem.UniqueId);
            }

            await ExecuteMailOperationAsync(package);
        }
        else if (markAsPreference == MailMarkAsOption.AfterDelay && PreferencesService.MarkAsDelay >= 0)
        {
            // TODO: Start a timer then queue.
        }
    }

    public void NotifyItemSelected()
    {
        OnPropertyChanged(nameof(SelectedMessageText));

        SelectedFolderPivot?.SelectedItemCount = SelectedItemsCount;
    }

    public void SetDragState(bool isDragInProgress, int draggingItemsCount = 0)
    {
        IsDragInProgress = isDragInProgress;
        DraggingItemsCount = isDragInProgress ? Math.Max(1, draggingItemsCount) : 0;
    }

    private void NotifyItemFoundState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsFolderEmpty));
    }

    private async void PreferencesServiceChanged(object sender, string propertyName)
    {
        if (propertyName == nameof(IPreferencesService.IsThreadingEnabled))
        {
            RefreshMailListOptions();

            if (ActiveFolder != null)
            {
                await InitializeFolderAsync();
            }

            return;
        }

        if (propertyName == nameof(IPreferencesService.IsMailListGroupHeadersEnabled))
        {
            RefreshMailListOptions();

            if (ActiveFolder != null)
            {
                await InitializeFolderAsync();
            }

            return;
        }

        if (propertyName == nameof(IPreferencesService.IsNewestThreadMailFirst))
        {
            RefreshMailListOptions();
            return;
        }

        if (propertyName == nameof(IPreferencesService.AccountNicknamePosition))
        {
            UpdateAccountNicknamePositionForItems();
            return;
        }

        if (propertyName is nameof(IPreferencesService.UndoSendingDraftsIntervalInSeconds) or nameof(IPreferencesService.UndoDeletingMailsIntervalInSeconds))
        {
            OnPropertyChanged(nameof(UndoMailActionBarDismissInterval));
            return;
        }

        if (propertyName != nameof(IPreferencesService.IsShowEmptyJunkFolderEnabled))
            return;

        await ExecuteUIThread(() =>
        {
            OnPropertyChanged(nameof(IsEmptyFolderButtonVisible));
            EmptyFolderCommand.NotifyCanExecuteChanged();
        });
    }

    private void UpdateBarMessage(InfoBarMessageType severity, string title, string message)
    {
        BarSeverity = severity;
        BarTitle = title;
        BarMessage = message;

        IsBarOpen = true;
    }

    private async Task<bool> UpdateFolderPivotsAsync(
        IBaseFolderMenuItem folder,
        long expectedFolderChangeGeneration)
    {
        if (folder == null)
            return false;

        var isInSearchMode = false;
        bool? selectedFocus = null;
        await ExecuteUIThread(() =>
        {
            isInSearchMode = IsInSearchMode;
            selectedFocus = SelectedFolderPivot?.IsFocused;
        }).ConfigureAwait(false);

        var pivots = new List<FolderPivotViewModel>();
        if (isInSearchMode)
        {
            pivots.Add(new FolderPivotViewModel(Translator.SearchPivotName, selectedFocus));
        }
        else
        {
            if (folder is IMailCategoryMenuItem or IMergedMailCategoryMenuItem)
            {
                pivots.Add(new FolderPivotViewModel(folder.FolderName, null));
            }
            // Merged folders don't support focused feature.
            else if (folder is IMergedAccountFolderMenuItem)
            {
                pivots.Add(new FolderPivotViewModel(folder.FolderName, null));
            }
            else if (folder is IFolderMenuItem singleFolderMenuItem)
            {
                var parentAccount = singleFolderMenuItem.ParentAccount;

                bool isFocusedInboxEnabled = await _accountService
                    .IsAccountFocusedEnabledAsync(parentAccount.Id)
                    .ConfigureAwait(false);
                if (expectedFolderChangeGeneration != Volatile.Read(ref folderChangeGeneration))
                    return false;

                bool isInboxFolder = folder.SpecialFolderType == SpecialFolderType.Inbox;

                // Folder supports Focused - Other
                if (isInboxFolder && isFocusedInboxEnabled)
                {
                    // Can be passed as empty string. Focused - Other will be used regardless.
                    var focusedItem = new FolderPivotViewModel(string.Empty, true);
                    var otherItem = new FolderPivotViewModel(string.Empty, false);

                    pivots.Add(focusedItem);
                    pivots.Add(otherItem);
                }
                else
                {
                    // If the account and folder doesn't support focused feature, just add itself.
                    pivots.Add(new FolderPivotViewModel(singleFolderMenuItem.FolderName, null));
                }
            }
        }

        if (pivots.Count == 0)
            pivots.Add(new FolderPivotViewModel(folder.FolderName, null));

        if (expectedFolderChangeGeneration != Volatile.Read(ref folderChangeGeneration))
            return false;

        var applied = false;
        await ExecuteUIThread(() =>
        {
            if (expectedFolderChangeGeneration != Volatile.Read(ref folderChangeGeneration))
                return;

            PivotFolders.Clear();
            foreach (var pivot in pivots)
            {
                PivotFolders.Add(pivot);
            }

            SelectedFolderPivot = pivots[0];
            lastRequestedPivot = SelectedFolderPivot;
            applied = true;
        });

        return applied;
    }

    #region Commands

    [RelayCommand]
    public Task ExecuteHoverAction(MailOperationPreperationRequest request) => ExecuteMailOperationAsync(request);

    [RelayCommand]
    private async Task ExecuteTopBarAction(IMenuOperation menuItem)
    {
        if (menuItem is not MailOperationMenuItem mailOperationMenuItem || SelectedItemsCount == 0) return;

        await HandleMailOperation(mailOperationMenuItem.Operation, SelectedItems);
    }

    /// <summary>
    /// Executes the requested mail operation for currently selected items.
    /// </summary>
    /// <param name="operation">Action to execute for selected items.</param>
    [RelayCommand]
    private async Task ExecuteMailOperation(MailOperation mailOperation)
    {
        if (SelectedItemsCount == 0) return;

        await HandleMailOperation(mailOperation, SelectedItems);
    }

    [RelayCommand]
    private void RequestIdleDelete()
        => RequestIdleMailOperation(MailOperation.SoftDelete);

    [RelayCommand]
    private void RequestIdleFlag()
        => RequestIdleMailOperation(MailOperation.SetFlag);

    [RelayCommand]
    private void RequestIdleToggleRead()
        => RequestIdleMailOperation(MailOperation.MarkAsRead);

    [RelayCommand]
    private void RequestIdleMove()
        => RequestIdleMailOperation(MailOperation.Move);

    [RelayCommand]
    private void UnselectAll()
        => Messenger.Send(new ClearMailSelectionsRequested());

    private void RequestIdleMailOperation(MailOperation operation)
        => Messenger.Send(new MailOperationRequested(
            operation,
            MailOperationTriggerSource.Idle));

    private async Task HandleMailOperation(MailOperation mailOperation, IEnumerable<MailItemViewModel> mailItems)
    {
        var mailItemList = mailItems?.Where(a => a?.MailCopy != null).ToList() ?? [];
        if (mailItemList.Count == 0) return;

        if (IsDraftCreationOperation(mailOperation))
        {
            await CreateDraftFromMailAsync(mailItemList.FirstOrDefault(), mailOperation);
            return;
        }

        var package = new MailOperationPreperationRequest(mailOperation, mailItemList.Select(a => a.MailCopy));

        await ExecuteMailOperationAsync(package);
    }

    private static bool IsDraftCreationOperation(MailOperation operation)
        => operation is MailOperation.Reply or MailOperation.ReplyAll or MailOperation.Forward;

    /// <summary>
    /// Sends a new message to synchronize current folder.
    /// </summary>
    [RelayCommand]
    private void SyncFolder()
    {
        if (!CanSynchronize) return;

        //_notificationBuilder.CreateNotificationsAsync(MailCollection.SelectedItems.Select(a => a.MailCopy));
        //return;

        // Only synchronize listed folders.

        // When doing linked inbox sync, we need to save the sync id to report progress back only once.
        // Otherwise, we will report progress for each folder and that's what we don't want.

        trackingSynchronizationId = Guid.NewGuid();
        completedTrackingSynchronizationCount = 0;

        foreach (var folder in ActiveFolder.HandlingFolders)
        {
            var options = new MailSynchronizationOptions()
            {
                AccountId = folder.MailAccountId,
                Type = MailSynchronizationType.CustomFolders,
                SynchronizationFolderIds = [folder.Id],
                GroupedSynchronizationTrackingId = trackingSynchronizationId
            };

            Messenger.Send(new NewMailSynchronizationRequested(options));
        }
    }

    [RelayCommand]
    private async Task SelectedPivotChanged(FolderPivotViewModel pivot)
    {
        if (pivot == null || ReferenceEquals(lastRequestedPivot, pivot))
            return;

        lastRequestedPivot = pivot;
        await InitializeFolderAsync();
    }

    [RelayCommand]
    private async Task SelectedSortingChanged(SortingOption option)
    {
        SelectedSortingOption = option;

        await InitializeFolderAsync();
    }

    [RelayCommand]
    private async Task SelectedFilterChanged(FilterOption option)
    {
        SelectedFilterOption = option;

        await InitializeFolderAsync();
    }

    [RelayCommand]
    public async Task PerformSearchAsync()
    {
        IsOnlineSearchEnabled = false;
        AreSearchResultsOnline = false;
        HasNoOnlineSearchResult = false;
        OnPropertyChanged(nameof(HasNoOnlineSearchResult));
        IsInSearchMode = !string.IsNullOrEmpty(SearchQuery);

        if (IsInSearchMode)
        {
            IsOnlineSearchButtonVisible = false;
        }

        if (ActiveFolder != null &&
            await UpdateFolderPivotsAsync(ActiveFolder, Volatile.Read(ref folderChangeGeneration)))
        {
            await InitializeFolderAsync();
        }
    }

    [RelayCommand]
    private async Task EnableFolderSynchronizationAsync()
    {
        if (ActiveFolder == null || IsCategoryView) return;

        foreach (var folder in ActiveFolder.HandlingFolders)
        {
            await _folderService.ChangeFolderSynchronizationStateAsync(folder.Id, true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEmptyFolder))]
    private async Task EmptyFolderAsync()
    {
        if (!IsJunkFolder || ActiveFolder == null) return;

        foreach (var folder in ActiveFolder.HandlingFolders.OfType<MailItemFolder>())
        {
            var folderPrepRequest = new FolderOperationPreperationRequest(FolderOperation.Empty, folder);
            await _winoRequestDelegator.ExecuteAsync(folderPrepRequest);
        }
    }

    private bool CanEmptyFolder() => IsEmptyFolderButtonVisible && !IsAccountSynchronizerInSynchronization;

    [RelayCommand(CanExecute = nameof(CanLoadMoreItems))]
    private async Task LoadMoreItemsAsync()
    {
        MailListLoadContext context;
        MailFetchCursor cursor;
        lock (mailLoadSync)
        {
            if (IsInitializingFolder ||
                IsOnlineSearchEnabled ||
                FinishedLoading ||
                isLoadingMore ||
                mailLoadCancellationTokenSource.IsCancellationRequested ||
                ActiveFolder == null ||
                SelectedFolderPivot == null ||
                SelectedFilterOption == null ||
                SelectedSortingOption == null)
            {
                return;
            }

            isLoadingMore = true;
            cursor = nextMailCursor;
            var handlingFolders = (!string.IsNullOrWhiteSpace(SearchQuery) && SearchHandlingFolders.Count > 0
                    ? SearchHandlingFolders
                    : ActiveFolder.HandlingFolders)
                .Where(folder => folder != null)
                .GroupBy(folder => folder.Id)
                .Select(group => group.First())
                .ToList();
            IReadOnlyList<Guid> categoryIds = ActiveFolder switch
            {
                IMailCategoryMenuItem singleCategory => [singleCategory.MailCategory.Id],
                IMergedMailCategoryMenuItem mergedCategory => mergedCategory.Categories.Select(category => category.Id).ToList(),
                _ => []
            };
            context = new(
                mailLoadGeneration,
                ActiveFolder,
                SelectedFolderPivot,
                SelectedFilterOption,
                SelectedSortingOption,
                SearchQuery ?? string.Empty,
                SearchCriteria,
                IsInSearchMode,
                IsOnlineSearchEnabled,
                handlingFolders,
                categoryIds,
                mailLoadCancellationTokenSource.Token);
        }

        var acquired = false;
        try
        {
            await ExecuteUIThread(() => IsInitializingFolder = true);
            await listManipulationSemepahore.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            acquired = true;
            context.CancellationToken.ThrowIfCancellationRequested();

            var options = CreateInitializationOptions(
                context,
                context.IsSearchMode ? context.Query : string.Empty,
                CreateExistingIdSet());
            var page = await _mailService
                .FetchMailPageAsync(options, cursor, context.CancellationToken)
                .ConfigureAwait(false);
            var viewModels = await PrepareMailViewModelsAsync(
                page.Items,
                context.HandlingFolders,
                context.CancellationToken).ConfigureAwait(false);
            var pendingOperationUniqueIds = await GetPendingOperationUniqueIdsForActiveFolderAccountsAsync(
                context.HandlingFolders,
                context.CancellationToken).ConfigureAwait(false);
            ApplyPendingOperationBusyStates(viewModels, pendingOperationUniqueIds);
            if (!IsCurrentMailLoad(context))
                return;

            await MailCollection.AddRangeAsync(
                viewModels,
                clearIdCache: false,
                shouldApply: () => IsCurrentMailLoad(context));
            if (!IsCurrentMailLoad(context))
                return;

            lock (mailLoadSync)
            {
                if (IsCurrentMailLoad(context))
                {
                    nextMailCursor = page.NextCursor;
                }
            }

            await ExecuteUIThread(() =>
            {
                if (IsCurrentMailLoad(context))
                {
                    FinishedLoading = !page.HasMore;
                }
            });
        }
        catch (OperationCanceledException)
        {
            // A newer folder/filter request owns the list now.
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load the next mail page.");
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }

            lock (mailLoadSync)
            {
                if (context.Generation == mailLoadGeneration)
                {
                    isLoadingMore = false;
                }
            }

            if (context.Generation == Volatile.Read(ref mailLoadGeneration))
            {
                await ExecuteUIThread(() =>
                {
                    if (IsCurrentMailLoad(context))
                    {
                        IsInitializingFolder = false;
                    }
                });
            }
        }
    }

    #endregion

    public Task ExecuteMailOperationAsync(MailOperationPreperationRequest package) => _winoRequestDelegator.ExecuteAsync(package);

    [RelayCommand]
    private async Task UndoLatestQueuedActionAsync()
    {
        var accountIds = CurrentVisibleUndoMailActionPack?.AccountIds
            .Where(accountId => accountId != Guid.Empty)
            .Distinct()
            .ToList();

        if (accountIds == null || accountIds.Count == 0)
        {
            accountIds = ActiveFolder?.HandlingFolders
                .Select(folder => folder.MailAccountId)
                .Where(accountId => accountId != Guid.Empty)
                .Distinct()
                .ToList() ?? [];
        }

        foreach (var accountId in accountIds)
        {
            await _synchronizationManager.UndoLatestQueuedAction(accountId);
        }

        await ExecuteUIThread(() =>
        {
            IsUndoMailActionBarOpen = false;
            CurrentVisibleUndoMailActionPack = null;
        });
    }

    public override async Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args)
    {
        if (args.Handled || args.Mode != WinoApplicationMode.Mail)
            return;

        var targetItems = GetShortcutTargetItems().ToList();

        switch (args.Action)
        {
            case KeyboardShortcutAction.ToggleReadUnread:
                if (!targetItems.Any()) return;
                await ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.MarkAsRead, targetItems.Select(x => x.MailCopy), true));
                args.Handled = true;
                break;
            case KeyboardShortcutAction.ToggleFlag:
                if (!targetItems.Any()) return;
                await ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.SetFlag, targetItems.Select(x => x.MailCopy), true));
                args.Handled = true;
                break;
            case KeyboardShortcutAction.ToggleArchive:
                if (!targetItems.Any()) return;
                await ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Archive, targetItems.Select(x => x.MailCopy), true));
                args.Handled = true;
                break;
            case KeyboardShortcutAction.Delete:
                if (!targetItems.Any()) return;
                await ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.SoftDelete, targetItems.Select(x => x.MailCopy)));
                args.Handled = true;
                break;
            case KeyboardShortcutAction.Move:
                if (!targetItems.Any()) return;
                await ExecuteMailOperationAsync(new MailOperationPreperationRequest(MailOperation.Move, targetItems.Select(x => x.MailCopy)));
                args.Handled = true;
                break;
            case KeyboardShortcutAction.Reply:
                await CreateDraftForShortcutTargetAsync(DraftCreationReason.Reply);
                args.Handled = true;
                break;
            case KeyboardShortcutAction.ReplyAll:
                await CreateDraftForShortcutTargetAsync(DraftCreationReason.ReplyAll);
                args.Handled = true;
                break;
        }
    }

    private IEnumerable<MailItemViewModel> GetShortcutTargetItems()
    {
        if (SelectedItemsCount > 0)
            return SelectedItems;

        if (_activeMailItem != null)
            return new[] { _activeMailItem };

        return Array.Empty<MailItemViewModel>();
    }

    private async Task CreateDraftForShortcutTargetAsync(DraftCreationReason reason)
    {
        var targetMail = GetShortcutTargetItems().FirstOrDefault();
        await CreateDraftFromMailAsync(targetMail, reason);
    }

    public Task CreateDraftFromMailAsync(MailItemViewModel targetMail, MailOperation operation)
    {
        var reason = operation switch
        {
            MailOperation.Reply => DraftCreationReason.Reply,
            MailOperation.ReplyAll => DraftCreationReason.ReplyAll,
            MailOperation.Forward => DraftCreationReason.Forward,
            _ => DraftCreationReason.Empty
        };

        return CreateDraftFromMailAsync(targetMail, reason);
    }

    private async Task CreateDraftFromMailAsync(MailItemViewModel targetMail, DraftCreationReason reason)
    {
        if (reason == DraftCreationReason.Empty)
            return;

        if (targetMail?.MailCopy == null || targetMail.MailCopy.FileId == Guid.Empty)
            return;

        var mimeInformation = await _mimeFileService.GetMimeMessageInformationAsync(targetMail.MailCopy.FileId, targetMail.MailCopy.AssignedAccount.Id);
        if (mimeInformation?.MimeMessage == null)
            return;

        var draftOptions = new DraftCreationOptions
        {
            Reason = reason,
            ReferencedMessage = new ReferencedMessage
            {
                MimeMessage = mimeInformation.MimeMessage,
                MailCopy = targetMail.MailCopy
            }
        };

        try
        {
            var (draftMailCopy, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(targetMail.MailCopy.AssignedAccount.Id, draftOptions).ConfigureAwait(false);
            var draftPreparationRequest = new DraftPreparationRequest(targetMail.MailCopy.AssignedAccount, draftMailCopy, draftBase64MimeMessage, draftOptions.Reason, targetMail.MailCopy);
            await _winoRequestDelegator.ExecuteAsync(draftPreparationRequest);
        }
        catch (MimePersistenceException ex)
        {
            _mailDialogService.InfoBarMessage(Translator.Info_DraftCreationFailed, ex.Message, InfoBarMessageType.Error);
        }
        catch (UnavailableSpecialFolderException ex)
        {
            _mailDialogService.InfoBarMessage(
                Translator.Info_MissingFolderTitle,
                string.Format(Translator.Info_MissingFolderMessage, ex.SpecialFolderType),
                InfoBarMessageType.Warning,
                Translator.SettingConfigureSpecialFolders_Button,
                () => _mailDialogService.HandleSystemFolderConfigurationDialogAsync(ex.AccountId, _folderService));
        }
    }

    public IEnumerable<MailOperationMenuItem> GetAvailableMailActions(IEnumerable<MailItemViewModel> contextMailItems)
        => _contextMenuItemService.GetMailItemContextMenuActions(contextMailItems.Select(a => a.MailCopy));

    public async Task RetryDraftUploadAsync(MailItemViewModel draftItem)
    {
        try
        {
            await _draftSyncRetryService.RetryNowAsync(draftItem?.MailCopy);
        }
        catch (Exception ex)
        {
            _mailDialogService.InfoBarMessage(Translator.Info_RequestCreationFailedTitle, ex.Message, InfoBarMessageType.Error);
        }
    }

    private void RefreshMailListOptions()
    {
        var sortByName = SelectedSortingOption?.Type == SortingOptionType.Sender;
        MailListOptions = new MailListProjectionOptions
        {
            SortMode = sortByName ? MailListSortMode.Name : MailListSortMode.Date,
            GroupMode = !PreferencesService.IsMailListGroupHeadersEnabled
                ? MailListGroupMode.None
                : sortByName
                    ? MailListGroupMode.Name
                    : MailListGroupMode.Date,
            IsThreadingEnabled = PreferencesService.IsThreadingEnabled,
            ThreadMessageOrder = PreferencesService.IsNewestThreadMailFirst
                ? ThreadMessageOrder.NewestFirst
                : ThreadMessageOrder.OldestFirst,
            IsPinnedFirst = true,
        };
    }

    public async Task<(IReadOnlyList<MailCategory> Categories, IReadOnlyCollection<Guid> AssignedCategoryIds)> GetAvailableCategoriesAsync(IEnumerable<MailItemViewModel> targetItems)
    {
        var targetList = targetItems?.Where(a => a?.MailCopy?.AssignedAccount != null).ToList() ?? [];
        if (targetList.Count == 0)
            return ([], []);

        var accountIds = targetList.Select(a => a.MailCopy.AssignedAccount.Id).Distinct().ToList();
        if (accountIds.Count != 1)
            return ([], []);

        var accountId = accountIds[0];
        var uniqueIds = targetList.Select(a => a.MailCopy.UniqueId).Distinct().ToList();

        var categories = await _mailCategoryService.GetCategoriesAsync(accountId).ConfigureAwait(false);
        var assignedCategoryIds = await _mailCategoryService.GetAssignedCategoryIdsForAllAsync(uniqueIds).ConfigureAwait(false);

        return (categories, assignedCategoryIds);
    }

    public async Task ToggleCategoryAssignmentAsync(MailCategory category, IEnumerable<MailItemViewModel> targetItems, bool isAssignedToAll)
    {
        var targetList = targetItems?.Where(a => a?.MailCopy?.AssignedAccount != null).ToList() ?? [];
        if (category == null || targetList.Count == 0)
            return;

        var accountIds = targetList.Select(a => a.MailCopy.AssignedAccount.Id).Distinct().ToList();
        if (accountIds.Count != 1)
            return;

        var accountId = accountIds[0];
        var uniqueIds = targetList.Select(a => a.MailCopy.UniqueId).Distinct().ToList();

        if (isAssignedToAll)
        {
            await _mailCategoryService.UnassignCategoryAsync(category.Id, uniqueIds).ConfigureAwait(false);
        }
        else
        {
            await _mailCategoryService.AssignCategoryAsync(category.Id, uniqueIds).ConfigureAwait(false);
        }

        await ApplyCategoryAssignmentToVisibleItemsAsync(category, targetList, isAssignedToAll).ConfigureAwait(false);

        if (targetList.First().MailCopy.AssignedAccount.ProviderType != MailProviderType.Outlook)
            return;

        var requests = new List<IRequestBase>();
        foreach (var mailItem in targetList.Select(a => a.MailCopy).GroupBy(a => a.UniqueId).Select(group => group.First()))
        {
            var categoryNames = await _mailCategoryService.GetCategoryNamesForMailAsync(mailItem.UniqueId).ConfigureAwait(false);
            requests.Add(new MailCategoryAssignmentRequest(mailItem, category.Id, category.Name, categoryNames, !isAssignedToAll));
        }

        await _winoRequestDelegator.ExecuteAsync(accountId, requests).ConfigureAwait(false);
    }

    private async Task ApplyCategoryAssignmentToVisibleItemsAsync(
        MailCategory category,
        IReadOnlyList<MailItemViewModel> targetItems,
        bool wasAssignedToAll)
    {
        if (category == null || targetItems == null || targetItems.Count == 0)
            return;

        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync().ConfigureAwait(false);
            acquired = true;

            await ExecuteUIThread(() =>
            {
                foreach (var targetItem in targetItems.GroupBy(a => a.UniqueId).Select(a => a.First()))
                {
                    if (targetItem?.MailCopy == null)
                        continue;

                    if (TryGetUpdatedCategories(targetItem.Categories, category, wasAssignedToAll, out var updatedCategories))
                    {
                        targetItem.UpdateCategories(updatedCategories);
                    }
                }
            });

            if (IsCategoryView && wasAssignedToAll)
            {
                await RemoveItemsWithoutActiveSeedAsync(targetItems.Select(item => item.UniqueId));
                await ExecuteUIThread(NotifyItemFoundState);
            }
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }
    }

    private static bool TryGetUpdatedCategories(IReadOnlyList<MailCategory> currentCategories,
                                                MailCategory category,
                                                bool wasAssignedToAll,
                                                out IReadOnlyList<MailCategory> updatedCategories)
    {
        currentCategories ??= [];

        if (wasAssignedToAll)
        {
            if (!currentCategories.Any(a => a?.Id == category.Id))
            {
                updatedCategories = currentCategories;
                return false;
            }

            updatedCategories = currentCategories
                .Where(a => a?.Id != category.Id)
                .ToList();
            return true;
        }

        if (currentCategories.Any(a => a?.Id == category.Id))
        {
            updatedCategories = currentCategories;
            return false;
        }

        updatedCategories = currentCategories
            .Where(a => a != null)
            .Append(category)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return true;
    }

    public Task ChangePinnedStatusAsync(IEnumerable<MailItemViewModel> targetItems, bool isPinned)
    {
        var uniqueIds = targetItems?
            .Where(a => a?.MailCopy != null)
            .Select(a => a.MailCopy.UniqueId)
            .Distinct()
            .ToList() ?? [];

        return _mailService.ChangePinnedStatusAsync(uniqueIds, isPinned);
    }

    private bool ShouldPreventItemAdd(MailCopy mailItem)
    {
        if (mailItem == null || SelectedFilterOption == null)
            return true;

        bool condition = mailItem.IsRead
                           && SelectedFilterOption.Type == FilterOptionType.Unread
                           || !mailItem.IsFlagged
                           && SelectedFilterOption.Type == FilterOptionType.Flagged
                           || !mailItem.HasAttachments
                           && SelectedFilterOption.Type == FilterOptionType.Files;

        return condition;
    }

    private static bool IsDraftOrSentFolder(MailCopy mailItem)
        => mailItem?.AssignedFolder?.SpecialFolderType is SpecialFolderType.Draft or SpecialFolderType.Sent;

    private bool IsActiveDraftFolder()
        => ActiveFolder?.SpecialFolderType == SpecialFolderType.Draft;

    private bool BelongsToActiveFolder(MailCopy mailItem)
        => !IsCategoryView && mailItem?.AssignedFolder != null && ActiveFolder?.HandlingFolders?.Any(a => a.Id == mailItem.AssignedFolder.Id) == true;

    private bool ShouldIncludeByThread(MailCopy mailItem)
        => PreferencesService.IsThreadingEnabled
           && !string.IsNullOrEmpty(mailItem?.ThreadId)
           && ThreadIdExistsInCollection(mailItem);

    private bool ShouldIncludeAddedMailInCurrentList(MailCopy addedMail)
    {
        if (addedMail == null || ActiveFolder == null || addedMail.AssignedFolder == null)
            return false;

        // 1) If threading is enabled and we already have the same conversation in view, include it.
        if (ShouldIncludeByThread(addedMail))
            return true;

        // 2) Include items that belong to the active folder or category.
        if (MatchesActiveFolderOrCategory(addedMail))
            return true;

        // 3) Draft-specific visibility: include drafts while viewing Drafts.
        if (addedMail.IsDraft && IsActiveDraftFolder())
            return true;

        return false;
    }

    private bool MatchesActiveFolderOrCategory(MailCopy mail)
    {
        if (mail == null || ActiveFolder == null)
            return false;

        if (!IsCategoryView)
            return BelongsToActiveFolder(mail);

        IReadOnlySet<Guid> activeCategoryIds = ActiveFolder switch
        {
            IMailCategoryMenuItem singleCategory => new HashSet<Guid> { singleCategory.MailCategory.Id },
            IMergedMailCategoryMenuItem mergedCategory => mergedCategory.Categories
                .Select(category => category.Id)
                .ToHashSet(),
            _ => new HashSet<Guid>()
        };

        return mail.Categories?.Any(category => activeCategoryIds.Contains(category.Id)) == true;
    }

    private bool ShouldExcludeAddedMailByFocusedPivot(MailCopy addedMail)
    {
        // Conversations already shown in the list should receive their new/restored children
        // even when the child mail belongs to the other focused inbox pivot.
        if (ShouldIncludeByThread(addedMail))
            return false;

        return SelectedFolderPivot?.IsFocused is bool isFocused && addedMail.IsFocused != isFocused;
    }

    private bool IsMailMatchingLocalSearch(MailCopy mailItem)
    {
        if (!IsInSearchMode) return true;
        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;

        var query = SearchQuery.Trim();

        return (!string.IsNullOrEmpty(mailItem.Subject) && mailItem.Subject.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(mailItem.PreviewText) && mailItem.PreviewText.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(mailItem.FromName) && mailItem.FromName.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(mailItem.FromAddress) && mailItem.FromAddress.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesActiveListSeed(MailCopy mail)
    {
        if (mail == null || ActiveFolder == null || SelectedFolderPivot == null)
            return false;

        var folderMatch = MatchesActiveFolderOrCategory(mail) ||
                          (mail.IsDraft && IsActiveDraftFolder());
        if (!folderMatch || ShouldPreventItemAdd(mail))
            return false;

        if (SelectedFolderPivot.IsFocused is bool isFocused && mail.IsFocused != isFocused)
            return false;

        return !IsInSearchMode ||
               (!IsOnlineSearchEnabled &&
                !AreSearchResultsOnline &&
               IsMailMatchingLocalSearch(mail));
    }

    private bool ShouldIncludeLiveMail(MailCopy mail)
    {
        if (!ShouldIncludeAddedMailInCurrentList(mail) ||
            ShouldPreventItemAdd(mail) ||
            ShouldExcludeAddedMailByFocusedPivot(mail))
        {
            return false;
        }

        if (!IsInSearchMode)
            return true;

        return !IsOnlineSearchEnabled &&
               !AreSearchResultsOnline &&
               IsMailMatchingLocalSearch(mail);
    }

    private bool ThreadHasActiveSeed(string threadId) =>
        !string.IsNullOrWhiteSpace(threadId) &&
        ((IEnumerable<MailItemViewModel>)MailCollection.Items).Any(item =>
            string.Equals(item.ThreadId, threadId, StringComparison.Ordinal) &&
            MatchesActiveListSeed(item.MailCopy));

    [RelayCommand]
    public void RemoveFirst()
    {
        var fi = MailCollection.GetFirst();
        if (fi == null) return;

        Messenger.Send(new MailRemovedMessage(fi.MailCopy, EntityUpdateSource.Server));
    }

    /// <summary>
    /// Checks if a ThreadId exists in the current mail collection.
    /// </summary>
    /// <param name="mailItem">The mail item to check ThreadId for.</param>
    /// <returns>True if the ThreadId exists in the collection, false otherwise.</returns>
    private bool ThreadIdExistsInCollection(MailCopy mailItem)
    {
        return MailCollection.ContainsThreadId(mailItem.ThreadId);
    }

    protected override async void OnMailAdded(MailCopy addedMail, EntityUpdateSource source)
    {
        base.OnMailAdded(addedMail, source);

        if (addedMail.AssignedAccount == null || addedMail.AssignedFolder == null) return;
        if (ShouldSuppressDraftAdd(addedMail)) return;

        bool hasLock = false;

        try
        {
            await listManipulationSemepahore.WaitAsync();
            hasLock = true;

            if (ActiveFolder == null) return;

            if (IsCategoryView)
            {
                var handlingFolders = ActiveFolder.HandlingFolders?.ToList() ?? [];
                await PopulateMailCategoriesAsync(new[] { addedMail }, handlingFolders, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // Re-evaluate folder membership after acquiring the semaphore so an add that was queued
            // behind a folder re-initialization cannot land in the newly selected folder by mistake.
            if (!ActiveFolder.HandlingFolders.Any(a => a.MailAccountId == addedMail.AssignedAccount.Id)) return;

            // Fix for draft duplication: When a draft is created for reply/forward, it's first added as local draft.
            // Then the server sync fetches it back. We should skip adding remote drafts if a local draft already exists
            // with the same ThreadId. The mapping system (DraftMapped) will handle updating the existing local draft.
            if (addedMail.IsDraft && !addedMail.IsLocalDraft && !string.IsNullOrEmpty(addedMail.ThreadId))
            {
                // Check if collection already has a local draft with the same ThreadId in the same folder
                bool hasLocalDraftInSameThread = false;

                foreach (var mailItem in MailCollection.Items)
                {
                    if (mailItem.IsDraft &&
                        mailItem.MailCopy.IsLocalDraft &&
                        mailItem.MailCopy.ThreadId == addedMail.ThreadId &&
                        mailItem.MailCopy.FolderId == addedMail.FolderId)
                    {
                        hasLocalDraftInSameThread = true;
                        break;
                    }
                }

                if (hasLocalDraftInSameThread)
                {
                    // Local draft exists in the same thread - skip adding remote duplicate
                    // The mapping system will update the local draft with remote IDs when DraftMapped message is received
                    return;
                }
            }

            if (ShouldSuppressDraftAdd(addedMail) ||
                !ShouldIncludeLiveMail(addedMail))
            {
                return;
            }

            // AddAsync already handles UI threading internally, no need to wrap it
            await MailCollection.AddAsync(addedMail);

            if (source == EntityUpdateSource.ClientUpdated)
            {
                await ExecuteUIThread(() =>
                {
                    var addedItem = MailCollection.Find(addedMail.UniqueId);
                    if (addedItem != null)
                    {
                        addedItem.IsBusy = true;
                    }
                });
            }

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to add mail {MailUniqueId} to the active list.", addedMail.UniqueId);
        }
        finally
        {
            if (hasLock)
            {
                listManipulationSemepahore.Release();
            }
        }
    }

    protected override async void OnMailUpdated(MailCopy updatedMail, EntityUpdateSource source, MailCopyChangeFlags changedProperties)
    {
        base.OnMailUpdated(updatedMail, source, changedProperties);
        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            if (IsCategoryView)
            {
                var handlingFolders = ActiveFolder?.HandlingFolders?.ToList() ?? [];
                await PopulateMailCategoriesAsync(new[] { updatedMail }, handlingFolders, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            bool isItemListed = MailCollection.ContainsMailUniqueId(updatedMail.UniqueId);
            if (!isItemListed)
            {
                if (!ShouldSuppressDraftAdd(updatedMail) &&
                    ShouldIncludeLiveMail(updatedMail))
                {
                    await MailCollection.AddAsync(updatedMail);
                    await ExecuteUIThread(NotifyItemFoundState);
                }

                return;
            }

            await MailCollection.UpdateMailCopy(updatedMail, source, changedProperties);

            var listedItem = MailCollection.Find(updatedMail.UniqueId);
            if (listedItem == null)
                return;

            if (PreferencesService.IsThreadingEnabled &&
                !string.IsNullOrWhiteSpace(listedItem.ThreadId))
            {
                if (!ThreadHasActiveSeed(listedItem.ThreadId))
                {
                    var threadIds = ((IEnumerable<MailItemViewModel>)MailCollection.Items)
                        .Where(item => string.Equals(item.ThreadId, listedItem.ThreadId, StringComparison.Ordinal))
                        .Select(item => item.UniqueId)
                        .ToArray();
                    await MailCollection.RemoveRangeByIdAsync(threadIds);
                }
            }
            else if (!MatchesActiveListSeed(listedItem.MailCopy))
            {
                await MailCollection.RemoveAsync(listedItem.MailCopy);
            }

            await ExecuteUIThread(NotifyItemFoundState);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update mail {MailUniqueId} in the active list.", updatedMail?.UniqueId);
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }

        await ExecuteUIThread(() => { SetupTopBarActions(); });
    }

    protected override async void OnMailStateUpdated(MailStateChange updatedState, EntityUpdateSource source)
    {
        base.OnMailStateUpdated(updatedState, source);

        if (updatedState == null)
            return;

        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            if (!MailCollection.ContainsMailUniqueId(updatedState.UniqueId))
                return;

            await MailCollection.UpdateMailStateAsync(updatedState, source);
            var listedItem = MailCollection.Find(updatedState.UniqueId);
            if (listedItem != null && !MatchesActiveListSeed(listedItem.MailCopy))
            {
                if (PreferencesService.IsThreadingEnabled &&
                    !string.IsNullOrWhiteSpace(listedItem.ThreadId))
                {
                    if (!ThreadHasActiveSeed(listedItem.ThreadId))
                    {
                        var threadIds = ((IEnumerable<MailItemViewModel>)MailCollection.Items)
                            .Where(item => string.Equals(item.ThreadId, listedItem.ThreadId, StringComparison.Ordinal))
                            .Select(item => item.UniqueId)
                            .ToArray();
                        await MailCollection.RemoveRangeByIdAsync(threadIds);
                    }
                }
                else
                {
                    await MailCollection.RemoveAsync(listedItem.MailCopy);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update state for mail {MailUniqueId}.", updatedState.UniqueId);
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }

        await ExecuteUIThread(() =>
        {
            NotifyItemFoundState();
            SetupTopBarActions();
        });
    }

    protected override async void OnBulkMailStateUpdated(IReadOnlyList<MailStateChange> updatedStates, EntityUpdateSource source)
    {
        var targetStates = updatedStates?
            .Where(x => x != null)
            .GroupBy(x => x.UniqueId)
            .Select(group => group.Last())
            .ToList() ?? [];

        if (targetStates.Count == 0)
            return;

        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            var listedStates = targetStates
                .Where(state => MailCollection.ContainsMailUniqueId(state.UniqueId))
                .ToList();

            if (listedStates.Count == 0)
                return;

            await MailCollection.UpdateMailStatesAsync(listedStates, source);
            await RemoveItemsWithoutActiveSeedAsync(
                listedStates.Select(state => state.UniqueId));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to apply a bulk mail-state update.");
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }

        await ExecuteUIThread(() =>
        {
            NotifyItemFoundState();
            SetupTopBarActions();
        });
    }

    protected override async void OnBulkMailUpdated(IReadOnlyList<MailCopy> updatedMails, EntityUpdateSource source, MailCopyChangeFlags changedProperties)
    {
        var targetMails = updatedMails?
            .Where(x => x != null)
            .GroupBy(x => x.UniqueId)
            .Select(group => group.First())
            .ToList() ?? [];

        if (targetMails.Count == 0)
            return;

        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            if (IsCategoryView)
            {
                var handlingFolders = ActiveFolder?.HandlingFolders?.ToList() ?? [];
                await PopulateMailCategoriesAsync(targetMails, handlingFolders, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var listedMails = targetMails
                .Where(mail => MailCollection.ContainsMailUniqueId(mail.UniqueId))
                .ToList();
            var additions = targetMails
                .Where(mail => !MailCollection.ContainsMailUniqueId(mail.UniqueId))
                .Where(ShouldIncludeLiveMail)
                .Select(CreateMailItemViewModel)
                .ToList();

            if (listedMails.Count > 0)
            {
                await MailCollection.UpdateMailCopiesAsync(listedMails, source, changedProperties);
                await RemoveItemsWithoutActiveSeedAsync(
                    listedMails.Select(mail => mail.UniqueId));
            }

            if (additions.Count > 0)
            {
                await MailCollection.AddRangeAsync(additions, clearIdCache: false);
            }

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
                SetupTopBarActions();
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to apply a bulk mail update.");
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }
    }

    private async Task RemoveItemsWithoutActiveSeedAsync(IEnumerable<Guid> candidateIds)
    {
        var candidates = candidateIds
            .Select(MailCollection.Find)
            .Where(item => item != null)
            .ToList();
        var idsToRemove = new HashSet<Guid>();

        foreach (var item in candidates)
        {
            if (MatchesActiveListSeed(item.MailCopy))
                continue;

            if (PreferencesService.IsThreadingEnabled &&
                !string.IsNullOrWhiteSpace(item.ThreadId))
            {
                if (!ThreadHasActiveSeed(item.ThreadId))
                {
                    idsToRemove.UnionWith(((IEnumerable<MailItemViewModel>)MailCollection.Items)
                        .Where(threadItem => string.Equals(threadItem.ThreadId, item.ThreadId, StringComparison.Ordinal))
                        .Select(threadItem => threadItem.UniqueId));
                }
            }
            else
            {
                idsToRemove.Add(item.UniqueId);
            }
        }

        if (idsToRemove.Count > 0)
        {
            await MailCollection.RemoveRangeByIdAsync(idsToRemove);
        }
    }

    protected override async void OnMailRemoved(MailCopy removedMail, EntityUpdateSource source)
    {
        base.OnMailRemoved(removedMail, source);

        if (removedMail.AssignedAccount == null) return;

        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            // Remove only if this specific mail copy currently exists in this list.
            // Using AssignedFolder-based checks is unreliable for move flows because the
            // same MailCopy instance can be updated before this message is handled.
            bool removedItemExistsInCurrentList = MailCollection.ContainsMailUniqueId(removedMail.UniqueId);

            bool isDeletedByGmailUnreadFolderAction = ActiveFolder?.SpecialFolderType == SpecialFolderType.Unread &&
                                                      gmailUnreadFolderMarkedAsReadUniqueIds.Contains(removedMail.UniqueId);

            if (removedItemExistsInCurrentList && !isDeletedByGmailUnreadFolderAction)
            {
                MailItemViewModel nextItem = null;
                bool isDeletedMailSelected = false;

                await ExecuteUIThread(() =>
                {
                    isDeletedMailSelected = IsMailSelected(removedMail.UniqueId);

                    if (isDeletedMailSelected && PreferencesService.AutoSelectNextItem)
                    {
                        nextItem = MailCollection.GetNextItem(removedMail);
                    }
                });

                // RemoveAsync already handles UI threading internally
                await MailCollection.RemoveAsync(removedMail);
                await PruneDraftThreadOrphansAsync(removedMail.ThreadId);

                if (nextItem != null)
                    WeakReferenceMessenger.Default.Send(new SelectMailItemContainerEvent(nextItem.UniqueId, ScrollToItem: true));
                // If there is no replacement, the threaded list projection drops the
                // removed selection token and publishes the resulting empty snapshot.

                await ExecuteUIThread(() => { NotifyItemFoundState(); });
            }
            else if (isDeletedByGmailUnreadFolderAction)
            {
                // Remove the entry from the set so we can listen to actual deletes next time.
                gmailUnreadFolderMarkedAsReadUniqueIds.Remove(removedMail.UniqueId);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove mail {MailUniqueId} from the active list.", removedMail.UniqueId);
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }
    }

    protected override async void OnBulkMailRemoved(IReadOnlyList<MailCopy> removedMails, EntityUpdateSource source)
    {
        var targetMails = removedMails?
            .Where(x => x != null && x.AssignedAccount != null)
            .GroupBy(x => x.UniqueId)
            .Select(group => group.First())
            .ToList() ?? [];

        if (targetMails.Count == 0)
            return;

        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            var existingMails = targetMails
                .Where(mail => MailCollection.ContainsMailUniqueId(mail.UniqueId))
                .ToList();

            if (existingMails.Count == 0)
                return;

            await MailCollection.RemoveRangeAsync(existingMails);

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
                SetupTopBarActions();
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove a bulk mail update from the active list.");
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }
    }

    protected override async void OnBulkMailAdded(IReadOnlyList<MailCopy> addedMails, EntityUpdateSource source)
    {
        var targetMails = addedMails?
            .Where(x => x != null)
            .GroupBy(x => x.UniqueId)
            .Select(group => group.First())
            .Where(mail => !ShouldSuppressDraftAdd(mail))
            .ToList() ?? [];

        if (targetMails.Count == 0)
            return;

        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            if (IsCategoryView)
            {
                var handlingFolders = ActiveFolder?.HandlingFolders?.ToList() ?? [];
                await PopulateMailCategoriesAsync(targetMails, handlingFolders, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var mailsToAdd = targetMails
                .Where(mail => !MailCollection.ContainsMailUniqueId(mail.UniqueId))
                .Where(mail => !ShouldSuppressDraftAdd(mail))
                .Where(ShouldIncludeLiveMail)
                .ToList();

            if (mailsToAdd.Count == 0)
                return;

            await MailCollection.AddRangeAsync(mailsToAdd.Select(CreateMailItemViewModel), false);

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
                SetupTopBarActions();
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to add a bulk mail update to the active list.");
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }
    }

    protected override async void OnFolderDeleted(MailItemFolder folder)
    {
        base.OnFolderDeleted(folder);

        if (ActiveFolder == null) return;

        var acquired = false;
        try
        {
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            var isActiveFolder = ActiveFolder?.HandlingFolders.Any(a => a.Id == folder.Id) == true;
            if (isActiveFolder)
            {
                await MailCollection.ClearAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove deleted folder {FolderId} from the active list.", folder.Id);
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }
    }

    protected override async void OnDraftCreated(MailCopy draftMail, MailAccount account)
    {
        base.OnDraftCreated(draftMail, account);

        // Drafts always live in the account's Draft folder. When the active listing doesn't
        // display that folder, the draft must not be injected into the list. Open the composer
        // in a detached state instead so the user stays in the folder they are working on.
        if (!ShouldIncludeAddedMailInCurrentList(draftMail))
        {
            await ExecuteUIThread(() =>
            {
                var detachedDraft = CreateMailItemViewModel(draftMail);
                detachedDraft.ShouldFocusComposerOnOpen = true;

                Messenger.Send(new ComposeDetachedDraftRequested(detachedDraft));
            });

            return;
        }

        var acquired = false;
        try
        {
            // If the draft is created in another folder, we need to wait for that folder to be initialized.
            // Otherwise the draft mail item will be duplicated on the next add execution.
            await listManipulationSemepahore.WaitAsync();
            acquired = true;

            // AddAsync already handles UI threading internally
            await MailCollection.AddAsync(draftMail);
            await ExecuteUIThread(() =>
            {
                var draftItem = MailCollection.Find(draftMail.UniqueId);
                if (draftItem != null)
                {
                    draftItem.ShouldFocusComposerOnOpen = true;
                }

                // New draft is created by user. Bring the selected item into view.
                Messenger.Send(new SelectMailItemContainerEvent(draftMail.UniqueId, ScrollToItem: true));

                NotifyItemFoundState();
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to add draft {MailUniqueId} to the active list.", draftMail.UniqueId);
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }
        }
    }

    private bool ShouldSuppressDraftAdd(MailCopy mail)
    {
        if (mail?.IsDraft != true || mail.AssignedAccount == null)
            return false;

        return _synchronizationManager.IsDeleteRequestQueued(
            mail.AssignedAccount.Id,
            mail.UniqueId);
    }

    protected override void OnDraftMapped(string localDraftCopyId, string remoteDraftCopyId)
    {
        base.OnDraftMapped(localDraftCopyId, remoteDraftCopyId);

        // When a draft is mapped from local to remote, the database has been updated
        // but the UI collection still references the MailCopy object with old IDs.
        // The MailCollection.AddAsync method checks UniqueId (which doesn't change during mapping)
        // so if mapping worked correctly, no duplicate should appear.
        // This method is here for future enhancements if additional UI updates are needed.
    }

    private async Task<List<MailItemViewModel>> PrepareMailViewModelsAsync(
        IEnumerable<MailCopy> mailItems,
        IReadOnlyList<IMailItemFolder> handlingFolders = null,
        CancellationToken cancellationToken = default)
    {
        await PopulateMailCategoriesAsync(mailItems, handlingFolders, cancellationToken).ConfigureAwait(false);

        // Run ViewModel creation on background thread to avoid blocking UI
        return await Task.Run(() =>
        {
            var viewModels = new List<MailItemViewModel>();
            foreach (var mailItem in mailItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                viewModels.Add(CreateMailItemViewModel(mailItem));
            }
            return viewModels;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PopulateMailCategoriesAsync(
        IEnumerable<MailCopy> mailItems,
        IReadOnlyList<IMailItemFolder> handlingFolders,
        CancellationToken cancellationToken)
    {
        var mails = mailItems?.Where(a => a != null).ToList() ?? [];
        if (mails.Count == 0)
            return;

        var accountIdsByFolderId = handlingFolders?
            .GroupBy(a => a.Id)
            .ToDictionary(a => a.Key, a => a.First().MailAccountId) ?? new Dictionary<Guid, Guid>();

        var mailsByAccount = mails
            .GroupBy(mail => ResolveMailAccountId(mail, accountIdsByFolderId))
            .Where(group => group.Key != Guid.Empty)
            .ToList();

        foreach (var groupedMails in mailsByAccount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var categoriesByMail = await _mailCategoryService
                .GetCategoriesByMailAsync(groupedMails.Key, groupedMails.Select(a => a.UniqueId))
                .ConfigureAwait(false);

            foreach (var mail in groupedMails)
            {
                mail.Categories = categoriesByMail.TryGetValue(mail.UniqueId, out var categories)
                    ? categories.ToList()
                    : [];
            }
        }
    }

    private async Task<HashSet<Guid>> GetPendingOperationUniqueIdsForActiveFolderAccountsAsync(
        IReadOnlyList<IMailItemFolder> handlingFolders = null,
        CancellationToken cancellationToken = default)
    {
        var pendingOperationUniqueIds = new HashSet<Guid>();

        var accountIds = handlingFolders?
            .Select(folder => folder.MailAccountId)
            .Where(accountId => accountId != Guid.Empty)
            .Distinct()
            .ToList();

        if (accountIds == null || accountIds.Count == 0)
            return pendingOperationUniqueIds;

        foreach (var accountId in accountIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var synchronizer = await SynchronizationManager.Instance.GetSynchronizerAsync(accountId).ConfigureAwait(false);

            if (synchronizer == null)
                continue;

            foreach (var uniqueId in synchronizer.GetPendingOperationUniqueIds())
            {
                pendingOperationUniqueIds.Add(uniqueId);
            }
        }

        return pendingOperationUniqueIds;
    }

    private static void ApplyPendingOperationBusyStates(IEnumerable<MailItemViewModel> viewModels, HashSet<Guid> pendingOperationUniqueIds)
    {
        if (viewModels == null || pendingOperationUniqueIds == null || pendingOperationUniqueIds.Count == 0)
            return;

        foreach (var viewModel in viewModels)
        {
            viewModel.IsBusy = pendingOperationUniqueIds.Contains(viewModel.MailCopy.UniqueId);
        }
    }

    private sealed record MailListLoadContext(
        long Generation,
        IBaseFolderMenuItem Folder,
        FolderPivotViewModel Pivot,
        FilterOption Filter,
        SortingOption Sorting,
        string Query,
        MailSearchCriteria SearchCriteria,
        bool IsSearchMode,
        bool IsOnlineSearch,
        IReadOnlyList<IMailItemFolder> HandlingFolders,
        IReadOnlyList<Guid> CategoryIds,
        CancellationToken CancellationToken,
        MailListLoadTrace Trace = null);

    private MailListLoadContext BeginMailLoad()
    {
        if (ActiveFolder == null ||
            SelectedFolderPivot == null ||
            SelectedFilterOption == null ||
            SelectedSortingOption == null)
        {
            return null;
        }

        CancellationTokenSource cancellationTokenSource;
        long generation;
        lock (mailLoadSync)
        {
            mailLoadCancellationTokenSource.Cancel();
            mailLoadCancellationTokenSource.Dispose();
            mailLoadCancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource = mailLoadCancellationTokenSource;
            generation = ++mailLoadGeneration;
            nextMailCursor = null;
            isLoadingMore = false;
        }

        var folder = ActiveFolder;
        var handlingFolders = (!string.IsNullOrWhiteSpace(SearchQuery) && SearchHandlingFolders.Count > 0
                ? SearchHandlingFolders
                : folder.HandlingFolders)
            .Where(item => item != null)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();
        var categoryIds = folder switch
        {
            IMailCategoryMenuItem singleCategory => [singleCategory.MailCategory.Id],
            IMergedMailCategoryMenuItem mergedCategory => mergedCategory.Categories.Select(category => category.Id).ToList(),
            _ => []
        };

        // A previous trace that never reached its final frame is reported now rather than lost.
        ReportMailLoadTrace(MailListLoadTrace.Current);

        var trace = MailListLoadTrace.Begin(generation);
        trace.Completed += ReportMailLoadTrace;

        return new(
            generation,
            folder,
            SelectedFolderPivot,
            SelectedFilterOption,
            SelectedSortingOption,
            SearchQuery ?? string.Empty,
            SearchCriteria,
            IsInSearchMode,
            IsOnlineSearchEnabled,
            handlingFolders,
            categoryIds,
            cancellationTokenSource.Token,
            trace);
    }

    private bool IsCurrentMailLoad(MailListLoadContext context) =>
        context != null &&
        context.Generation == Volatile.Read(ref mailLoadGeneration) &&
        !context.CancellationToken.IsCancellationRequested;

    private static bool MatchesSemanticPostFilters(MailCopy item, MailSearchCriteria criteria)
    {
        if (!string.IsNullOrWhiteSpace(criteria.Sender) &&
            !string.Equals(item.FromAddress, criteria.Sender, StringComparison.OrdinalIgnoreCase))
            return false;

        var receivedUtc = item.CreationDate.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(item.CreationDate)
            : new DateTimeOffset(item.CreationDate.ToUniversalTime());
        if (criteria.ReceivedAfterUtc is { } after && receivedUtc < after)
            return false;
        if (criteria.ReceivedBeforeUtc is { } before && receivedUtc >= before)
            return false;

        return (!criteria.HasAttachments || item.HasAttachments) &&
               (!criteria.IsUnread || !item.IsRead) &&
               (!criteria.IsFlagged || item.IsFlagged);
    }

    private void CancelActiveMailLoad()
    {
        lock (mailLoadSync)
        {
            mailLoadCancellationTokenSource.Cancel();
            mailLoadGeneration++;
            nextMailCursor = null;
            isLoadingMore = false;
        }
    }

    private void CompletePendingFolderNavigation(bool result)
    {
        TaskCompletionSource<bool> completion;
        lock (mailLoadSync)
        {
            completion = pendingFolderCompletion;
            pendingFolderCompletion = null;
        }

        completion?.TrySetResult(result);
    }

    public async Task<bool> WaitForCurrentFolderInitializationAsync()
    {
        Task<bool> pendingInitialization;
        lock (mailLoadSync)
        {
            pendingInitialization = pendingFolderCompletion?.Task;
        }

        return pendingInitialization is null || await pendingInitialization.ConfigureAwait(false);
    }

    private MailListInitializationOptions CreateInitializationOptions(
        MailListLoadContext context,
        string searchQuery,
        System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> existingUniqueIds,
        List<MailCopy> preFetchedMailCopies = null,
        bool deduplicateByServerId = false,
        bool preservePreFetchedOrder = false)
    {
        var options = new MailListInitializationOptions(context.HandlingFolders,
                                                        context.Filter.Type,
                                                        context.Sorting.Type,
                                                        PreferencesService.IsThreadingEnabled,
                                                        context.Pivot.IsFocused,
                                                        searchQuery,
                                                        existingUniqueIds,
                                                        preFetchedMailCopies,
                                                        DeduplicateByServerId: deduplicateByServerId,
                                                        PreservePreFetchedOrder: preservePreFetchedOrder)
        {
            Sender = context.SearchCriteria.Sender,
            ReceivedAfterUtc = context.SearchCriteria.ReceivedAfterUtc,
            ReceivedBeforeUtc = context.SearchCriteria.ReceivedBeforeUtc,
            RequireAttachments = context.SearchCriteria.HasAttachments,
            RequireUnread = context.SearchCriteria.IsUnread,
            RequireFlagged = context.SearchCriteria.IsFlagged,
        };

        if (context.CategoryIds.Count == 0)
            return options;

        return options with
        {
            CategoryIds = context.CategoryIds
        };
    }

    private Task PruneDraftThreadOrphansAsync(string threadId)
    {
        if (!IsActiveDraftFolder() || string.IsNullOrWhiteSpace(threadId))
        {
            return Task.CompletedTask;
        }

        var remainingThreadItems = ((IEnumerable<MailItemViewModel>)MailCollection.Items)
            .Where(item => string.Equals(item.ThreadId, threadId, StringComparison.Ordinal))
            .ToArray();
        if (remainingThreadItems.Any(static item => item.IsDraft))
        {
            return Task.CompletedTask;
        }

        return MailCollection.RemoveRangeByIdAsync(
            remainingThreadItems
                .Where(static item => !item.IsDraft)
                .Select(static item => item.UniqueId));
    }

    private ConcurrentDictionary<Guid, bool> CreateExistingIdSet() =>
        new(MailCollection.ItemIds.Select(static id =>
            new KeyValuePair<Guid, bool>(id, true)));

    [RelayCommand]
    private async Task PerformOnlineSearchAsync()
    {
        IsOnlineSearchButtonVisible = false;
        IsOnlineSearchEnabled = true;

        await InitializeFolderAsync();
    }

    private async Task<List<MailCopy>> PerformSynchronizerOnlineSearchAsync(MailSearchCriteria criteria,
                                                                             IEnumerable<IMailItemFolder> handlingFolders,
                                                                             CancellationToken cancellationToken)
    {
        if (handlingFolders == null) return [];

        var distinctFolders = handlingFolders
            .Where(folder => folder != null)
            .GroupBy(folder => folder.Id)
            .Select(group => group.First())
            .ToList();

        var foldersByAccount = distinctFolders
            .GroupBy(a => a.MailAccountId)
            .ToList();

        if (foldersByAccount.Count == 0) return [];

        var searchTasks = foldersByAccount.Select(async groupedFolders =>
        {
            try
            {
                var synchronizer = await SynchronizationManager.Instance.GetSynchronizerAsync(groupedFolders.Key).ConfigureAwait(false);
                if (synchronizer == null) return (Results: new List<MailCopy>(), FailedAccount: string.Empty);

                var remoteCriteria = new RemoteMailSearchCriteria(
                    criteria.Query,
                    criteria.Sender,
                    criteria.ReceivedAfterUtc,
                    criteria.ReceivedBeforeUtc,
                    criteria.HasAttachments,
                    criteria.IsUnread,
                    criteria.IsFlagged);
                var accountResults = await synchronizer.OnlineSearchAsync(remoteCriteria, groupedFolders.ToList(), cancellationToken).ConfigureAwait(false);
                return (Results: accountResults ?? new List<MailCopy>(), FailedAccount: string.Empty);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Online search failed for account {AccountId}.", groupedFolders.Key);
                var account = await _accountService.GetAccountAsync(groupedFolders.Key).ConfigureAwait(false);
                return (Results: new List<MailCopy>(), FailedAccount: account?.Name ?? groupedFolders.Key.ToString());
            }
        });

        var allResults = await Task.WhenAll(searchTasks).ConfigureAwait(false);
        var failedAccounts = allResults.Select(result => result.FailedAccount).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
        if (failedAccounts.Length > 0)
        {
            await ExecuteUIThread(() => _mailDialogService.InfoBarMessage(
                Translator.OnlineSearch_PartialTitle,
                string.Format(Translator.OnlineSearch_PartialMessage, string.Join(", ", failedAccounts)),
                InfoBarMessageType.Warning));
        }

        var accountIdsByFolderId = distinctFolders.ToDictionary(folder => folder.Id, folder => folder.MailAccountId);
        var preferredFolderIds = distinctFolders.Select(folder => folder.Id).ToHashSet();

        return DeduplicateOnlineSearchResults(allResults.SelectMany(result => result.Results), accountIdsByFolderId, preferredFolderIds);
    }

    private static List<MailCopy> DeduplicateOnlineSearchResults(IEnumerable<MailCopy> results,
                                                                 IReadOnlyDictionary<Guid, Guid> accountIdsByFolderId,
                                                                 ISet<Guid> preferredFolderIds)
    {
        if (results == null) return [];

        return results
            .Where(mail => mail != null)
            .GroupBy(mail => (ResolveMailAccountId(mail, accountIdsByFolderId), ResolveSearchMailId(mail)))
            .Select(group => group
                .OrderByDescending(mail => preferredFolderIds.Contains(mail.FolderId))
                .ThenByDescending(mail => mail.CreationDate)
                .ThenBy(mail => mail.FolderId)
                .ThenBy(mail => mail.UniqueId)
                .First())
            .ToList();
    }

    private static Guid ResolveMailAccountId(MailCopy mail, IReadOnlyDictionary<Guid, Guid> accountIdsByFolderId)
    {
        if (mail?.AssignedAccount != null)
            return mail.AssignedAccount.Id;

        if (mail != null && accountIdsByFolderId.TryGetValue(mail.FolderId, out var accountId))
            return accountId;

        return Guid.Empty;
    }

    private static string ResolveSearchMailId(MailCopy mail)
        => string.IsNullOrWhiteSpace(mail?.Id) ? mail?.UniqueId.ToString("N") ?? string.Empty : mail.Id;

    private async Task<bool> InitializeFolderAsync()
    {
        var context = BeginMailLoad();
        if (context == null)
            return false;

        var acquired = false;
        var publishedFinishedState = false;
        try
        {
            await ExecuteUIThread(() =>
            {
                IsInitializingFolder = true;
                FinishedLoading = false;
            });

            await listManipulationSemepahore
                .WaitAsync(context.CancellationToken)
                .ConfigureAwait(false);
            acquired = true;
            context.CancellationToken.ThrowIfCancellationRequested();

            // The previous folder's rows stay on screen until the new page is ready, so a
            // switch costs one atomic swap instead of a teardown followed by a rebuild.
            context.Trace?.Mark(MailListLoadStage.PivotsResolved);

            var isDoingSearch = !string.IsNullOrWhiteSpace(context.Query);
            var supportsOnlineSearch = true;
            if (isDoingSearch)
            {
                foreach (var accountId in context.HandlingFolders.Select(folder => folder.MailAccountId).Distinct())
                {
                    var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
                    if (account?.ProviderType == MailProviderType.POP3)
                    {
                        supportsOnlineSearch = false;
                        break;
                    }
                }
            }

            var isDoingSemanticSearch = isDoingSearch &&
                context.SearchCriteria.ExecutionMode == SearchMode.Semantic &&
                _intelligenceSearchService is not null;
            var isDoingOnlineSearch = isDoingSearch &&
                !isDoingSemanticSearch &&
                (context.SearchCriteria.ExecutionMode == SearchMode.Online || context.IsOnlineSearch);
            List<MailCopy> onlineSearchItems = null;

            if (isDoingSemanticSearch)
            {
                try
                {
                    await ExecuteUIThread(() => IsSemanticSearchBusy = true);
                    var semanticResult = await _intelligenceSearchService.SearchAsync(new IntelligenceSearchOptions(
                        context.Query,
                        context.HandlingFolders.OfType<MailItemFolder>().ToArray(),
                        IsUnread: context.SearchCriteria.IsUnread || context.Filter.Type == FilterOptionType.Unread ? true : null,
                        IsFlagged: context.SearchCriteria.IsFlagged || context.Filter.Type == FilterOptionType.Flagged ? true : null,
                        HasAttachments: context.SearchCriteria.HasAttachments || context.Filter.Type == FilterOptionType.Files ? true : null), context.CancellationToken).ConfigureAwait(false);
                    onlineSearchItems = semanticResult.Items
                        .Where(item => MatchesSemanticPostFilters(item, context.SearchCriteria))
                        .ToList();
                    if (semanticResult.Omissions.Count > 0 && IsCurrentMailLoad(context))
                    {
                        await ExecuteUIThread(() => _mailDialogService.InfoBarMessage(
                            Translator.SemanticSearch_PartialTitle,
                            Translator.SemanticSearch_PartialMessage,
                            InfoBarMessageType.Warning));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to perform semantic search.");
                    isDoingSemanticSearch = false;
                    if (IsCurrentMailLoad(context))
                    {
                        await ExecuteUIThread(() => _mailDialogService.InfoBarMessage(
                            Translator.GeneralTitle_Error,
                            ex.Message,
                            InfoBarMessageType.Warning));
                    }
                }
                finally
                {
                    await ExecuteUIThread(() => IsSemanticSearchBusy = false);
                }
            }

            if (isDoingOnlineSearch)
            {
                try
                {
                    onlineSearchItems = await PerformSynchronizerOnlineSearchAsync(
                        context.SearchCriteria,
                        context.HandlingFolders,
                        context.CancellationToken).ConfigureAwait(false);
                    if (IsCurrentMailLoad(context))
                    {
                        await ExecuteUIThread(() => AreSearchResultsOnline = true);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to perform online search.");
                    isDoingOnlineSearch = false;
                    onlineSearchItems = null;

                    if (IsCurrentMailLoad(context))
                    {
                        await ExecuteUIThread(() =>
                        {
                            IsOnlineSearchEnabled = false;
                            AreSearchResultsOnline = false;
                            var serverErrorMessage = string.Format(Translator.OnlineSearchFailed_Message, ex.Message);
                            _mailDialogService.InfoBarMessage(
                                Translator.GeneralTitle_Error,
                                serverErrorMessage,
                                InfoBarMessageType.Warning);
                        });
                    }
                }
            }

            var options = CreateInitializationOptions(
                context,
                isDoingOnlineSearch || isDoingSemanticSearch ? string.Empty : context.Query,
                new ConcurrentDictionary<Guid, bool>(),
                onlineSearchItems,
                isDoingOnlineSearch,
                isDoingSemanticSearch);

            context.Trace?.Mark(MailListLoadStage.QueryStarted);
            var page = await _mailService
                .FetchMailPageAsync(options, cancellationToken: context.CancellationToken)
                .ConfigureAwait(false);
            context.Trace?.Mark(MailListLoadStage.QueryCompleted);

            var viewModels = await PrepareMailViewModelsAsync(
                page.Items,
                context.HandlingFolders,
                context.CancellationToken).ConfigureAwait(false);
            var pendingOperationUniqueIds = await GetPendingOperationUniqueIdsForActiveFolderAccountsAsync(
                context.HandlingFolders,
                context.CancellationToken).ConfigureAwait(false);
            ApplyPendingOperationBusyStates(viewModels, pendingOperationUniqueIds);

            if (context.Trace is { } preparedTrace)
            {
                preparedTrace.ItemCount = viewModels.Count;
                preparedTrace.Mark(MailListLoadStage.ViewModelsPrepared);
            }

            if (!IsCurrentMailLoad(context))
                return false;

            // Selection belongs to the outgoing page, so it is cleared together with the swap
            // rather than ahead of the query.
            Messenger.Send(new ClearMailSelectionsRequested());

            context.Trace?.Mark(MailListLoadStage.StorePublishStarted);
            await MailCollection.ResetAsync(
                viewModels,
                shouldApply: () => IsCurrentMailLoad(context)).ConfigureAwait(false);
            if (!IsCurrentMailLoad(context))
                return false;

            lock (mailLoadSync)
            {
                if (IsCurrentMailLoad(context))
                {
                    nextMailCursor = page.NextCursor;
                }
            }

            await ExecuteUIThread(() =>
            {
                if (!IsCurrentMailLoad(context))
                    return;

                FinishedLoading = !page.HasMore;
                HasNoOnlineSearchResult = (isDoingOnlineSearch || isDoingSemanticSearch) && page.Items.Count == 0;
                OnPropertyChanged(nameof(HasNoOnlineSearchResult));
                IsOnlineSearchButtonVisible = supportsOnlineSearch && isDoingSearch && !isDoingOnlineSearch && !isDoingSemanticSearch;

                IsInitializingFolder = false;
                OnPropertyChanged(nameof(CanSynchronize));
                NotifyItemFoundState();
                IsBarOpen = false;
                publishedFinishedState = true;
            });

            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Mail initialization generation {Generation} was canceled.", context.Generation);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                context.IsSearchMode
                    ? "Failed to perform search for generation {Generation}."
                    : "Failed to refresh listed mails for generation {Generation}.",
                context.Generation);
            return false;
        }
        finally
        {
            if (acquired)
            {
                listManipulationSemepahore.Release();
            }

            // The happy path already published the finished state with the page in one hop.
            if (!publishedFinishedState && context.Generation == Volatile.Read(ref mailLoadGeneration))
            {
                await ExecuteUIThread(() =>
                {
                    if (!IsCurrentMailLoad(context))
                        return;

                    IsInitializingFolder = false;
                    OnPropertyChanged(nameof(CanSynchronize));
                    NotifyItemFoundState();
                    IsBarOpen = false;
                });
            }

            // On the happy path the trace reports itself once the new rows reach a frame,
            // which happens after this method has already returned.
            if (!publishedFinishedState)
            {
                ReportMailLoadTrace(context.Trace);
            }
        }
    }

    /// <summary>
    /// Emits the per-stage timings for one load so a slow folder switch can be attributed to
    /// the database, view-model construction, collection propagation, or template rendering.
    /// </summary>
    private void ReportMailLoadTrace(MailListLoadTrace trace)
    {
        if (trace is null || !trace.TryBeginReport())
        {
            return;
        }

        _logger.Debug(
            "Mail load generation {Generation} published {MailCount} mails as {RowCount} rows in {TotalMilliseconds:F1} ms. Stages: {Stages}.",
            trace.Generation,
            trace.ItemCount,
            trace.RowCount,
            trace.GetElapsed(MailListLoadStage.FirstFrameRendered) ??
                trace.GetElapsed(MailListLoadStage.ProjectionRebuildCompleted) ??
                trace.GetElapsed(MailListLoadStage.StoreApplied) ?? 0d,
            trace.Describe());
    }

    #region Receivers

    async void IRecipient<ActiveMailFolderChangedEvent>.Receive(ActiveMailFolderChangedEvent message)
    {
        var changeGeneration = Interlocked.Increment(ref folderChangeGeneration);
        TaskCompletionSource<bool> previousCompletion;
        lock (mailLoadSync)
        {
            previousCompletion = pendingFolderCompletion;
            pendingFolderCompletion = message.FolderInitLoadAwaitTask;
        }

        if (!ReferenceEquals(previousCompletion, message.FolderInitLoadAwaitTask))
        {
            previousCompletion?.TrySetResult(false);
        }

        try
        {
            await ExecuteUIThread(() =>
            {
                if (changeGeneration != Volatile.Read(ref folderChangeGeneration))
                    return;

                NotifyItemSelected();
                ActiveFolder = message.BaseFolderMenuItem;
                gmailUnreadFolderMarkedAsReadUniqueIds.Clear();
                trackingSynchronizationId = null;
                completedTrackingSynchronizationCount = 0;
                OnPropertyChanged(nameof(IsArchiveSpecialFolder));

                SelectedSortingOption = SortingOptions[0];
                SearchQuery = string.Empty;
                IsInSearchMode = false;
                IsOnlineSearchEnabled = false;
                IsOnlineSearchButtonVisible = false;
                AreSearchResultsOnline = false;
                HasNoOnlineSearchResult = false;
                OnPropertyChanged(nameof(HasNoOnlineSearchResult));
            });

            if (!await UpdateFolderPivotsAsync(message.BaseFolderMenuItem, changeGeneration))
            {
                message.FolderInitLoadAwaitTask?.TrySetResult(false);
                return;
            }

            var loaded = await InitializeFolderAsync();
            if (changeGeneration != Volatile.Read(ref folderChangeGeneration))
            {
                message.FolderInitLoadAwaitTask?.TrySetResult(false);
                return;
            }

            if (loaded)
            {
                await CheckIfAccountIsSynchronizingAsync();
            }

            message.FolderInitLoadAwaitTask?.TrySetResult(loaded);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to change the active mail folder.");
            message.FolderInitLoadAwaitTask?.TrySetResult(false);
        }
        finally
        {
            lock (mailLoadSync)
            {
                if (ReferenceEquals(pendingFolderCompletion, message.FolderInitLoadAwaitTask))
                {
                    pendingFolderCompletion = null;
                }
            }
        }
    }

    public async void Receive(AccountSynchronizationCompleted message)
    {
        await ExecuteUIThread(() =>
        {
            if (ActiveFolder == null) return;

            bool isLinkedInboxSyncResult = message.SynchronizationTrackingId == trackingSynchronizationId;

            if (isLinkedInboxSyncResult)
            {
                var isCompletedAccountListed = ActiveFolder.HandlingFolders.Any(a => a.MailAccountId == message.AccountId);

                if (isCompletedAccountListed) completedTrackingSynchronizationCount++;

                // Group sync is started but not all folders are synchronized yet. Don't report progress.
                if (completedTrackingSynchronizationCount < ActiveFolder.HandlingFolders.Count()) return;
            }

            bool isReportingActiveAccountResult = ActiveFolder.HandlingFolders.Any(a => a.MailAccountId == message.AccountId);

            if (!isReportingActiveAccountResult) return;

            // At this point either all folders or a single folder sync is completed.
            switch (message.Result)
            {
                case SynchronizationCompletedState.Success:
                    // No need to pop success message when executing requests all the time...
                    if (message.Type != MailSynchronizationType.ExecuteRequests)
                    {
                        UpdateBarMessage(InfoBarMessageType.Success, ActiveFolder.FolderName, Translator.SynchronizationFolderReport_Success);
                    }
                    break;
                case SynchronizationCompletedState.PartiallyCompleted:
                    UpdateBarMessage(InfoBarMessageType.Warning, ActiveFolder.FolderName, Translator.SynchronizationFolderReport_Failed);
                    break;
                case SynchronizationCompletedState.Failed:
                    UpdateBarMessage(InfoBarMessageType.Error, ActiveFolder.FolderName, Translator.SynchronizationFolderReport_Failed);
                    break;
                default:
                    break;
            }
        });
    }

    async void IRecipient<MailItemNavigationRequested>.Receive(MailItemNavigationRequested message)
    {
        if (message.UniqueMailId == Guid.Empty)
            return;

        var requestVersion = Interlocked.Increment(ref mailNavigationRequestVersion);

        try
        {
            var mailCopy = await _mailService.GetSingleMailItemAsync(message.UniqueMailId).ConfigureAwait(false);
            if (mailCopy is null || requestVersion != Volatile.Read(ref mailNavigationRequestVersion))
                return;

            var isTargetFolderActive = false;
            await ExecuteUIThread(() =>
            {
                isTargetFolderActive = ActiveFolder?.HandlingFolders
                    .Any(folder => folder.Id == mailCopy.FolderId) == true;
            }).ConfigureAwait(false);

            if (requestVersion != Volatile.Read(ref mailNavigationRequestVersion))
                return;

            if (isTargetFolderActive)
            {
                Messenger.Send(new SelectMailItemContainerEvent(message.UniqueMailId, message.ScrollToItem));
                return;
            }

            if (mailCopy.AssignedAccount is null || mailCopy.FolderId == Guid.Empty)
            {
                _logger.Warning("Mail navigation target has no assigned account or folder. MailUniqueId: {MailUniqueId}",
                    message.UniqueMailId);
                return;
            }

            // The shell owns account and folder navigation. Its AccountMenuItemExtended handler
            // switches the account, waits for folder initialization, and sends this request again
            // only after the target folder's mail collection is ready.
            Messenger.Send(new AccountMenuItemExtended(mailCopy.FolderId, mailCopy));
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to resolve mail navigation target. MailUniqueId: {MailUniqueId}",
                message.UniqueMailId);
        }
    }

    #endregion

    public async void Receive(NewMailSynchronizationRequested message)
        => await ExecuteUIThread(() => { OnPropertyChanged(nameof(CanSynchronize)); });

    protected override async void OnFolderSynchronizationEnabled(IMailItemFolder mailItemFolder)
    {
        var isActiveFolder = false;

        await ExecuteUIThread(() =>
        {
            isActiveFolder = ActiveFolder?.EntityId == mailItemFolder.Id;
            if (!isActiveFolder)
                return;

            ActiveFolder.UpdateFolder(mailItemFolder);

            OnPropertyChanged(nameof(CanSynchronize));
            OnPropertyChanged(nameof(IsFolderSynchronizationEnabled));
        });

        if (isActiveFolder)
        {
            await ExecuteUIThread(() => SyncFolderCommand?.Execute(null));
        }
    }

    public async void Receive(AccountSynchronizerStateChanged message)
        => await CheckIfAccountIsSynchronizingAsync();

    private async Task CheckIfAccountIsSynchronizingAsync()
    {
        bool isAnyAccountSynchronizing = false;
        List<Guid> accountIds = null;

        // Check each account that this page is listing folders from.
        // If any of the synchronizers are synchronizing, we disable sync.

        await ExecuteUIThread(() =>
        {
            accountIds = ActiveFolder?.HandlingFolders
                .Select(a => a.MailAccountId)
                .ToList() ?? [];
        });

        foreach (var accountId in accountIds)
        {
            if (SynchronizationManager.Instance.IsAccountSynchronizing(accountId))
            {
                isAnyAccountSynchronizing = true;
                break;
            }
        }

        await ExecuteUIThread(() => { IsAccountSynchronizerInSynchronization = isAnyAccountSynchronizing; });
    }

    public async void Receive(AccountCacheResetMessage message)
    {
        var appliesToActiveFolder = false;

        await ExecuteUIThread(() =>
        {
            appliesToActiveFolder =
                message.Reason == AccountCacheResetReason.ExpiredCache &&
                ActiveFolder?.HandlingFolders.Any(a => a.MailAccountId == message.AccountId) == true;
        });

        if (appliesToActiveFolder)
        {
            // ClearAsync already handles UI threading internally
            await MailCollection.ClearAsync();

            await ExecuteUIThread(() =>
            {
                _mailDialogService.InfoBarMessage(Translator.AccountCacheReset_Title, Translator.AccountCacheReset_Message, InfoBarMessageType.Warning);
            });
        }
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();

        MailCollection.CoreDispatcher = Dispatcher;
    }

    public void Receive(ThumbnailAdded message)
    {
        _ = MailCollection.UpdateThumbnailsForAddressAsync(message.Email);
    }

    public async void Receive(IntelligenceMetadataChanged message)
    {
        try
        {
            var visibleMails = ((IEnumerable<MailItemViewModel>)MailCollection.Items)
                .Where(item => message.Scope == IntelligenceMetadataChangeScope.DatabaseReset ||
                    item.MailCopy?.AssignedAccount?.Id == message.LocalAccountId)
                .Where(item => message.Scope != IntelligenceMetadataChangeScope.Messages ||
                    (RemoteMessageIdentity.TryCreate(item.MailCopy) is string remoteId && message.RemoteMessageIds.Contains(remoteId)))
                .Select(static item => item.MailCopy)
                .ToArray();

            if (visibleMails.Length == 0)
            {
                return;
            }

            if (message.Scope == IntelligenceMetadataChangeScope.Messages)
            {
                await _mailService.HydrateIntelligenceMetadataAsync(visibleMails).ConfigureAwait(false);
            }
            else
            {
                foreach (var mail in visibleMails)
                {
                    mail.IntelligenceMetadata = null;
                }
            }

            await MailCollection.UpdateMailCopiesAsync(
                visibleMails,
                EntityUpdateSource.Server,
                MailCopyChangeFlags.IntelligenceMetadata).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to refresh visible intelligence metadata.");
        }
    }

    public async void Receive(IntelligenceVisibilityChanged message)
    {
        await ExecuteUIThread(() =>
        {
            foreach (var mailItem in ((IEnumerable<MailItemViewModel>)MailCollection.Items).Where(item =>
                item.MailCopy?.AssignedAccount?.Id == message.LocalAccountId))
            {
                mailItem.RefreshIntelligenceTiles();
            }
        });
    }

    public async void Receive(LanguageChanged message)
    {
        await ExecuteUIThread(() =>
        {
            foreach (var mailItem in MailCollection.Items)
            {
                mailItem.RefreshIntelligenceTiles();
            }
        });
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<MailItemNavigationRequested>(this);
        Messenger.Register<ActiveMailFolderChangedEvent>(this);
        Messenger.Register<AccountSynchronizationCompleted>(this);
        Messenger.Register<NewMailSynchronizationRequested>(this);
        Messenger.Register<AccountSynchronizerStateChanged>(this);
        Messenger.Register<AccountCacheResetMessage>(this);
        Messenger.Register<ThumbnailAdded>(this);
        Messenger.Register<MailOperationRequested>(this);
        Messenger.Register<UndoableMailActionPackChanged>(this);
        Messenger.Register<IntelligenceMetadataChanged>(this);
        Messenger.Register<IntelligenceVisibilityChanged>(this);
        Messenger.Register<LanguageChanged>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<MailItemNavigationRequested>(this);
        Messenger.Unregister<ActiveMailFolderChangedEvent>(this);
        Messenger.Unregister<AccountSynchronizationCompleted>(this);
        Messenger.Unregister<NewMailSynchronizationRequested>(this);
        Messenger.Unregister<AccountSynchronizerStateChanged>(this);
        Messenger.Unregister<AccountCacheResetMessage>(this);
        Messenger.Unregister<ThumbnailAdded>(this);
        Messenger.Unregister<MailOperationRequested>(this);
        Messenger.Unregister<UndoableMailActionPackChanged>(this);
        Messenger.Unregister<IntelligenceMetadataChanged>(this);
        Messenger.Unregister<IntelligenceVisibilityChanged>(this);
        Messenger.Unregister<LanguageChanged>(this);
    }

    public async void Receive(MailOperationRequested message)
    {
        var mailItems = message.MailItems?.Count > 0
            ? message.MailItems
            : message.TriggerSource == MailOperationTriggerSource.Idle
                ? SelectedItems
                : [];

        if (mailItems.Count == 0)
        {
            return;
        }

        var mailCopies = mailItems.Select(static item => item.MailCopy);
        var toggleExecution = message.TriggerSource is
            MailOperationTriggerSource.Swipe or
            MailOperationTriggerSource.Idle or
            MailOperationTriggerSource.Hover;
        var package = new MailOperationPreperationRequest(
            message.Operation,
            mailCopies,
            toggleExecution);
        await ExecuteMailOperationAsync(package);
    }

    public async void Receive(UndoableMailActionPackChanged message)
    {
        if (message?.Pack == null)
            return;

        await ExecuteUIThread(() =>
        {
            if (message.State == UndoableMailActionPackState.Queued)
            {
                CurrentVisibleUndoMailActionPack = message.Pack;
                UndoMailActionBarTitle = message.Pack.Title;
                UndoMailActionBarSeverity = message.Pack.Severity;
                IsUndoMailActionBarOpen = true;
                return;
            }

            if (CurrentVisibleUndoMailActionPack?.Id != message.Pack.Id)
                return;

            IsUndoMailActionBarOpen = false;
            CurrentVisibleUndoMailActionPack = null;
        });
    }

}
