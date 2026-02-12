using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using MoreLinq;
using Nito.AsyncEx;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

public partial class MailListPageViewModel : MailBaseViewModel,
    IRecipient<MailItemNavigationRequested>,
    IRecipient<ActiveMailFolderChangedEvent>,
    IRecipient<AccountSynchronizationCompleted>,
    IRecipient<NewMailSynchronizationRequested>,
    IRecipient<AccountSynchronizerStateChanged>,
    IRecipient<AccountCacheResetMessage>,
    IRecipient<ThumbnailAdded>,
    IRecipient<PropertyChangedMessage<bool>>,
    IRecipient<SwipeActionRequested>
{
    private bool isChangingFolder = false;

    private Guid? trackingSynchronizationId = null;
    private int completedTrackingSynchronizationCount = 0;

    /* [Bug] Unread folder reads All emails automatically with setting "Mark as Read: When Selected" enabled 
     * https://github.com/bkaankose/Wino-Mail/issues/162
     * We store the UniqueIds of the mails that are marked as read in Gmail Unread folder
     * to prevent them from being removed from the list when they are marked as read.
     */

    private readonly HashSet<Guid> gmailUnreadFolderMarkedAsReadUniqueIds = [];

    public WinoMailCollection MailCollection { get; set; } = new WinoMailCollection();
    public ObservableCollection<FolderPivotViewModel> PivotFolders { get; set; } = [];
    public ObservableCollection<MailOperationMenuItem> ActionItems { get; set; } = [];

    private readonly SemaphoreSlim listManipulationSemepahore = new SemaphoreSlim(1);
    private CancellationTokenSource listManipulationCancellationTokenSource = new CancellationTokenSource();

    public INavigationService NavigationService { get; }
    public IStatePersistanceService StatePersistenceService { get; }
    public IPreferencesService PreferencesService { get; }
    public INewThemeService ThemeService { get; }

    private readonly IAccountService _accountService;
    private readonly IMailDialogService _mailDialogService;
    private readonly IMailService _mailService;
    private readonly INotificationBuilder _notificationBuilder;
    private readonly IFolderService _folderService;
    private readonly IContextMenuItemService _contextMenuItemService;
    private readonly IWinoRequestDelegator _winoRequestDelegator;
    private readonly IKeyPressService _keyPressService;
    private readonly IWinoLogger _winoLogger;
    private MailItemViewModel _activeMailItem;

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
    private bool isMultiSelectionModeEnabled;

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

    [ObservableProperty]
    private FilterOption _selectedFilterOption;
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

    /// <summary>
    /// Current folder that is being represented from the menu.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize))]
    [NotifyPropertyChangedFor(nameof(IsFolderSynchronizationEnabled))]
    public partial IBaseFolderMenuItem ActiveFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize))]
    public partial bool IsAccountSynchronizerInSynchronization { get; set; }

    public MailListPageViewModel(IMailDialogService dialogService,
                                 INavigationService navigationService,
                                 IAccountService accountService,
                                 IMailDialogService mailDialogService,
                                 IMailService mailService,
                                 IStatePersistanceService statePersistenceService,
                                 INotificationBuilder notificationBuilder,
                                 IFolderService folderService,
                                 IContextMenuItemService contextMenuItemService,
                                 IWinoRequestDelegator winoRequestDelegator,
                                 IKeyPressService keyPressService,
                                 IPreferencesService preferencesService,
                                 INewThemeService themeService,
                                 IWinoLogger winoLogger)
    {
        _winoLogger = winoLogger;
        _accountService = accountService;
        _mailDialogService = mailDialogService;
        _mailService = mailService;
        _folderService = folderService;
        _contextMenuItemService = contextMenuItemService;
        _winoRequestDelegator = winoRequestDelegator;
        _keyPressService = keyPressService;

        PreferencesService = preferencesService;
        ThemeService = themeService;
        StatePersistenceService = statePersistenceService;
        _notificationBuilder = notificationBuilder;
        NavigationService = navigationService;

        SelectedFilterOption = FilterOptions[0];
        SelectedSortingOption = SortingOptions[0];

        MailListLength = statePersistenceService.MailListPaneLength;

        //_selectionChangedThrottler = new ThrottledEventHandler(100, () =>
        //{
        //    _ = ExecuteUIThread(() =>
        //    {
        //        if (MailCollection.SelectedVisibleCount == 1)
        //        {
        //            ActiveMailItemChanged(MailCollection.SelectedVisibleItems.ElementAt(0));
        //        }
        //        else
        //        {
        //            // At this point, either we don't have any item selected 
        //            // or we have multiple item selected. In either case
        //            // there should be no active item.

        //            ActiveMailItemChanged(null);
        //        }

        //        NotifyItemSelected();
        //        SetupTopBarActions();
        //    });

        //    ThrottledSelectionChanged?.Invoke(this, EventArgs.Empty);
        //});
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        MailCollection.ItemSelectionChanged += MailItemSelectionChanged;
    }

    public override async void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        MailCollection.ItemSelectionChanged -= MailItemSelectionChanged;

        await MailCollection.ClearAsync();
        MailCollection.Cleanup();
    }

    private void MailItemSelectionChanged(object sender, EventArgs e)
    {
        if (MailCollection.HasSingleItemSelected)
        {
            var selectedItem = MailCollection.SelectedItems.ElementAtOrDefault(0);
            ActiveMailItemChanged(selectedItem);
        }
        else if (MailCollection.SelectedItemsCount == 0)
        {
            ActiveMailItemChanged(null);
        }

        NotifyItemFoundState();
        NotifyItemSelected();
        SetupTopBarActions();
    }

    private void SetupTopBarActions()
    {
        ActionItems.Clear();

        var actions = GetAvailableMailActions(MailCollection.SelectedItems);
        actions.ForEach(a => ActionItems.Add(a));
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
                    MailCollection.GroupingType = value.Type == SortingOptionType.ReceiveDate ? EmailGroupingType.ByDate : EmailGroupingType.ByFromName;
                }
            }
        }
    }

    public bool CanSynchronize => !IsAccountSynchronizerInSynchronization && IsFolderSynchronizationEnabled;
    public bool IsFolderSynchronizationEnabled => ActiveFolder?.IsSynchronizationEnabled ?? false;
    public bool IsArchiveSpecialFolder => ActiveFolder?.SpecialFolderType == SpecialFolderType.Archive;

    public string SelectedMessageText => IsDragInProgress
        ? string.Format(Translator.MailsDragging, DraggingItemsCount)
        : MailCollection.SelectedItemsCount > 0
            ? string.Format(Translator.MailsSelected, MailCollection.SelectedItemsCount)
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

        SelectedFolderPivot?.SelectedItemCount = MailCollection.SelectedItemsCount;
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

    private async void UpdateBarMessage(InfoBarMessageType severity, string title, string message)
    {
        await ExecuteUIThread(() =>
        {
            BarSeverity = severity;
            BarTitle = title;
            BarMessage = message;

            IsBarOpen = true;
        });
    }

    private async Task UpdateFolderPivotsAsync()
    {
        if (ActiveFolder == null) return;

        PivotFolders.Clear();
        SelectedFolderPivot = null;

        if (IsInSearchMode)
        {
            var isFocused = SelectedFolderPivot?.IsFocused;

            PivotFolders.Add(new FolderPivotViewModel(Translator.SearchPivotName, isFocused));
        }
        else
        {
            // Merged folders don't support focused feature.

            if (ActiveFolder is IMergedAccountFolderMenuItem)
            {
                PivotFolders.Add(new FolderPivotViewModel(ActiveFolder.FolderName, null));
            }
            else if (ActiveFolder is IFolderMenuItem singleFolderMenuItem)
            {
                var parentAccount = singleFolderMenuItem.ParentAccount;

                bool isFocusedInboxEnabled = await _accountService.IsAccountFocusedEnabledAsync(parentAccount.Id);
                bool isInboxFolder = ActiveFolder.SpecialFolderType == SpecialFolderType.Inbox;

                // Folder supports Focused - Other
                if (isInboxFolder && isFocusedInboxEnabled)
                {
                    // Can be passed as empty string. Focused - Other will be used regardless.
                    var focusedItem = new FolderPivotViewModel(string.Empty, true);
                    var otherItem = new FolderPivotViewModel(string.Empty, false);

                    PivotFolders.Add(focusedItem);
                    PivotFolders.Add(otherItem);
                }
                else
                {
                    // If the account and folder doesn't support focused feature, just add itself.
                    PivotFolders.Add(new FolderPivotViewModel(singleFolderMenuItem.FolderName, null));
                }
            }
        }



        // This will trigger refresh.
        SelectedFolderPivot = PivotFolders.FirstOrDefault();
    }

    #region Commands

    [RelayCommand]
    public Task ExecuteHoverAction(MailOperationPreperationRequest request) => ExecuteMailOperationAsync(request);

    [RelayCommand]
    private async Task ExecuteTopBarAction(MailOperationMenuItem menuItem)
    {
        if (menuItem == null || MailCollection.SelectedItemsCount == 0) return;

        await HandleMailOperation(menuItem.Operation, MailCollection.SelectedItems);
    }

    /// <summary>
    /// Executes the requested mail operation for currently selected items.
    /// </summary>
    /// <param name="operation">Action to execute for selected items.</param>
    [RelayCommand]
    private async Task ExecuteMailOperation(MailOperation mailOperation)
    {
        if (MailCollection.SelectedItemsCount == 0) return;

        await HandleMailOperation(mailOperation, MailCollection.SelectedItems);
    }

    private async Task HandleMailOperation(MailOperation mailOperation, IEnumerable<MailItemViewModel> mailItems)
    {
        if (!mailItems.Any()) return;

        var package = new MailOperationPreperationRequest(mailOperation, mailItems.Select(a => a.MailCopy));

        await ExecuteMailOperationAsync(package);
    }

    /// <summary>
    /// Sens a new message to synchronize current folder.
    /// </summary>
    [RelayCommand]
    private void SyncFolder()
    {
        if (!CanSynchronize) return;

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
    private async Task SelectedPivotChanged()
    {
        if (isChangingFolder) return;

        await InitializeFolderAsync();
    }

    [RelayCommand]
    private async Task SelectedSortingChanged(SortingOption option)
    {
        SelectedSortingOption = option;

        if (isChangingFolder) return;

        await InitializeFolderAsync();
    }

    [RelayCommand]
    private async Task SelectedFilterChanged(FilterOption option)
    {
        SelectedFilterOption = option;

        if (isChangingFolder) return;

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

        await UpdateFolderPivotsAsync();
    }

    [RelayCommand]
    private async Task EnableFolderSynchronizationAsync()
    {
        if (ActiveFolder == null) return;

        foreach (var folder in ActiveFolder.HandlingFolders)
        {
            await _folderService.ChangeFolderSynchronizationStateAsync(folder.Id, true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreItems))]
    private async Task LoadMoreItemsAsync()
    {
        if (IsInitializingFolder || IsOnlineSearchEnabled || FinishedLoading) return;

        Debug.WriteLine("Loading more...");
        await ExecuteUIThread(() => { IsInitializingFolder = true; });

        var initializationOptions = new MailListInitializationOptions(ActiveFolder.HandlingFolders,
                                                                      SelectedFilterOption.Type,
                                                                      SelectedSortingOption.Type,
                                                                      PreferencesService.IsThreadingEnabled,
                                                                      SelectedFolderPivot.IsFocused,
                                                                      IsInSearchMode ? SearchQuery : string.Empty,
                                                                      MailCollection.MailCopyIdHashSet);

        var items = await _mailService.FetchMailsAsync(initializationOptions).ConfigureAwait(false);

        if (items.Count == 0)
        {
            await ExecuteUIThread(() => { FinishedLoading = true; });

            return;
        }

        var viewModels = await PrepareMailViewModelsAsync(items).ConfigureAwait(false);

        await MailCollection.AddRangeAsync(viewModels, false);
        await ExecuteUIThread(() => { IsInitializingFolder = false; });
    }

    #endregion

    public Task ExecuteMailOperationAsync(MailOperationPreperationRequest package) => _winoRequestDelegator.ExecuteAsync(package);

    public IEnumerable<MailOperationMenuItem> GetAvailableMailActions(IEnumerable<MailItemViewModel> contextMailItems)
        => _contextMenuItemService.GetMailItemContextMenuActions(contextMailItems.Select(a => a.MailCopy));

    private bool ShouldPreventItemAdd(MailCopy mailItem)
    {
        bool condition = mailItem.IsRead
                          && SelectedFilterOption.Type == FilterOptionType.Unread
                          || !mailItem.IsFlagged
                          && SelectedFilterOption.Type == FilterOptionType.Flagged;

        return condition;
    }

    private static bool IsDraftOrSentFolder(MailCopy mailItem)
        => mailItem?.AssignedFolder?.SpecialFolderType is SpecialFolderType.Draft or SpecialFolderType.Sent;

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

    private bool ShouldRemoveUpdatedMailFromCurrentList(MailCopy updatedMail)
    {
        if (ActiveFolder == null || updatedMail?.AssignedFolder == null) return true;

        bool isFromDraftOrSentFolder = IsDraftOrSentFolder(updatedMail);

        if (!isFromDraftOrSentFolder && !ActiveFolder.HandlingFolders.Any(a => a.Id == updatedMail.AssignedFolder.Id))
        {
            return true;
        }

        if (isFromDraftOrSentFolder && !ThreadIdExistsInCollection(updatedMail))
        {
            return true;
        }

        if (ShouldPreventItemAdd(updatedMail))
        {
            return true;
        }

        if (SelectedFolderPivot?.IsFocused is bool isFocused && updatedMail.IsFocused != isFocused)
        {
            return true;
        }

        // Online search results are a server-provided snapshot. Keep current items stable.
        if (IsInSearchMode && (IsOnlineSearchEnabled || AreSearchResultsOnline))
        {
            return false;
        }

        return !IsMailMatchingLocalSearch(updatedMail);
    }

    [RelayCommand]
    public void RemoveFirst()
    {
        var fi = MailCollection.GetFirst();
        if (fi == null) return;

        Messenger.Send(new MailRemovedMessage(fi.MailCopy));
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

    protected override async void OnMailAdded(MailCopy addedMail)
    {
        base.OnMailAdded(addedMail);

        if (addedMail.AssignedAccount == null || addedMail.AssignedFolder == null) return;

        try
        {
            if (ActiveFolder == null) return;

            // At least one of the accounts we are listing must match with the account of the added mail.
            if (!ActiveFolder.HandlingFolders.Any(a => a.MailAccountId == addedMail.AssignedAccount.Id)) return;

            // Messages coming to sent or draft folder should only be inserted if their ThreadId exists in the collection.
            bool isFromDraftOrSentFolder = IsDraftOrSentFolder(addedMail);

            if (isFromDraftOrSentFolder)
            {
                // Fix for draft duplication: When a draft is created for reply/forward, it's first added as local draft.
                // Then the server sync fetches it back. We should skip adding remote drafts if a local draft already exists
                // with the same ThreadId. The mapping system (DraftMapped) will handle updating the existing local draft.
                if (addedMail.IsDraft && !addedMail.IsLocalDraft && !string.IsNullOrEmpty(addedMail.ThreadId))
                {
                    // Check if collection already has a local draft with the same ThreadId in the same folder
                    bool hasLocalDraftInSameThread = false;
                    
                    foreach (var group in MailCollection.MailItems)
                    {
                        foreach (var item in group)
                        {
                            if (item is MailItemViewModel mailItem)
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
                            else if (item is ThreadMailItemViewModel threadItem)
                            {
                                foreach (var threadEmail in threadItem.ThreadEmails)
                                {
                                    if (threadEmail.IsDraft &&
                                        threadEmail.MailCopy.IsLocalDraft &&
                                        threadEmail.MailCopy.ThreadId == addedMail.ThreadId &&
                                        threadEmail.MailCopy.FolderId == addedMail.FolderId)
                                    {
                                        hasLocalDraftInSameThread = true;
                                        break;
                                    }
                                }
                                if (hasLocalDraftInSameThread) break;
                            }
                        }
                        if (hasLocalDraftInSameThread) break;
                    }

                    if (hasLocalDraftInSameThread)
                    {
                        // Local draft exists in the same thread - skip adding remote duplicate
                        // The mapping system will update the local draft with remote IDs when DraftMapped message is received
                        return;
                    }
                }

                // Only add if the ThreadId exists in the collection (can be threaded with existing items)
                if (!ThreadIdExistsInCollection(addedMail)) return;
            }
            else
            {
                // Item does not belong to this folder.
                if (!ActiveFolder.HandlingFolders.Any(a => a.Id == addedMail.AssignedFolder.Id)) return;

                // Item should be prevented from being added to the list due to filter.
                if (ShouldPreventItemAdd(addedMail)) return;
            }

            if (SelectedFolderPivot?.IsFocused is bool isFocused && addedMail.IsFocused != isFocused)
            {
                return;
            }

            if (IsInSearchMode)
            {
                // Online search results are loaded from a dedicated query snapshot.
                // Ignore live additions while that snapshot is active.
                if (IsOnlineSearchEnabled || AreSearchResultsOnline) return;

                if (!IsMailMatchingLocalSearch(addedMail)) return;
            }

            await listManipulationSemepahore.WaitAsync();

            // AddAsync already handles UI threading internally, no need to wrap it
            await MailCollection.AddAsync(addedMail);

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
            });
        }
        catch { }
        finally
        {
            listManipulationSemepahore.Release();
        }
    }

    protected override async void OnMailUpdated(MailCopy updatedMail, MailUpdateSource source)
    {
        base.OnMailUpdated(updatedMail, source);

        try
        {
            await listManipulationSemepahore.WaitAsync();

            bool isItemListed = MailCollection.ContainsMailUniqueId(updatedMail.UniqueId);
            if (!isItemListed) return;

            if (ShouldRemoveUpdatedMailFromCurrentList(updatedMail))
            {
                await MailCollection.RemoveAsync(updatedMail);
                await ExecuteUIThread(() => { NotifyItemFoundState(); });
                return;
            }

            await MailCollection.UpdateMailCopy(updatedMail, source);
        }
        finally
        {
            listManipulationSemepahore.Release();
        }

        // await ExecuteUIThread(() => { SetupTopBarActions(); });
    }

    protected override async void OnMailRemoved(MailCopy removedMail)
    {
        base.OnMailRemoved(removedMail);

        if (removedMail.AssignedAccount == null) return;

        try
        {
            await listManipulationSemepahore.WaitAsync();

            // Remove only if this specific mail copy currently exists in this list.
            // Using AssignedFolder-based checks is unreliable for move flows because the
            // same MailCopy instance can be updated before this message is handled.
            bool removedItemExistsInCurrentList = MailCollection.ContainsMailUniqueId(removedMail.UniqueId);

            bool isDeletedByGmailUnreadFolderAction = ActiveFolder?.SpecialFolderType == SpecialFolderType.Unread &&
                                                      gmailUnreadFolderMarkedAsReadUniqueIds.Contains(removedMail.UniqueId);

            if (removedItemExistsInCurrentList && !isDeletedByGmailUnreadFolderAction)
            {
                bool isDeletedMailSelected = MailCollection.SelectedItems.Any(a => a.MailCopy.UniqueId == removedMail.UniqueId);

                // Automatically select the next item in the list if the setting is enabled.
                MailItemViewModel nextItem = null;

                if (isDeletedMailSelected && PreferencesService.AutoSelectNextItem)
                {
                    await ExecuteUIThread(() =>
                    {
                        nextItem = MailCollection.GetNextItem(removedMail);
                    });
                }

                // RemoveAsync already handles UI threading internally
                await MailCollection.RemoveAsync(removedMail);

                if (nextItem != null)
                    WeakReferenceMessenger.Default.Send(new SelectMailItemContainerEvent(nextItem.UniqueId, ScrollToItem: true));
                else if (isDeletedMailSelected)
                {
                    // There are no next item to select, but we removed the last item which was selected.
                    // Clearing selected item will dispose rendering page.

                    // UnselectAllAsync already handles UI threading internally
                    await MailCollection.UnselectAllAsync();
                }

                await ExecuteUIThread(() => { NotifyItemFoundState(); });
            }
            else if (isDeletedByGmailUnreadFolderAction)
            {
                // Remove the entry from the set so we can listen to actual deletes next time.
                gmailUnreadFolderMarkedAsReadUniqueIds.Remove(removedMail.UniqueId);
            }
        }
        finally
        {
            listManipulationSemepahore.Release();
        }
    }

    protected override async void OnFolderDeleted(MailItemFolder folder)
    {
        base.OnFolderDeleted(folder);

        if (ActiveFolder == null) return;

        bool isActiveFolder = ActiveFolder.HandlingFolders.Any(a => a.Id == folder.Id);

        if (isActiveFolder)
        {
            await MailCollection.ClearAsync();
        }
    }

    protected override async void OnDraftCreated(MailCopy draftMail, MailAccount account)
    {
        base.OnDraftCreated(draftMail, account);

        try
        {
            // If the draft is created in another folder, we need to wait for that folder to be initialized.
            // Otherwise the draft mail item will be duplicated on the next add execution.
            await listManipulationSemepahore.WaitAsync();

            // AddAsync already handles UI threading internally
            await MailCollection.AddAsync(draftMail);

            await ExecuteUIThread(() =>
            {
                // New draft is created by user. Select the item.
                Messenger.Send(new MailItemNavigationRequested(draftMail.UniqueId, ScrollToItem: true));

                NotifyItemFoundState();
            });
        }
        finally
        {
            listManipulationSemepahore.Release();
        }
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

    private async Task<List<MailItemViewModel>> PrepareMailViewModelsAsync(IEnumerable<MailCopy> mailItems, CancellationToken cancellationToken = default)
    {
        // Run ViewModel creation on background thread to avoid blocking UI
        return await Task.Run(() =>
        {
            var viewModels = new List<MailItemViewModel>();
            foreach (var mailItem in mailItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                viewModels.Add(new MailItemViewModel(mailItem));
            }
            return viewModels;
        }, cancellationToken).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task PerformOnlineSearchAsync()
    {
        IsOnlineSearchButtonVisible = false;
        IsOnlineSearchEnabled = true;

        await InitializeFolderAsync();
    }

    private async Task<List<MailCopy>> PerformSynchronizerOnlineSearchAsync(string queryText,
                                                                             IEnumerable<IMailItemFolder> handlingFolders,
                                                                             CancellationToken cancellationToken)
    {
        if (handlingFolders == null) return [];

        var foldersByAccount = handlingFolders
            .GroupBy(a => a.MailAccountId)
            .ToList();

        if (foldersByAccount.Count == 0) return [];

        var searchTasks = foldersByAccount.Select(async groupedFolders =>
        {
            var synchronizer = await SynchronizationManager.Instance.GetSynchronizerAsync(groupedFolders.Key).ConfigureAwait(false);
            if (synchronizer == null) return new List<MailCopy>();

            var accountResults = await synchronizer.OnlineSearchAsync(queryText, groupedFolders.ToList(), cancellationToken).ConfigureAwait(false);
            return accountResults ?? new List<MailCopy>();
        });

        var allResults = await Task.WhenAll(searchTasks).ConfigureAwait(false);

        return allResults
            .SelectMany(a => a)
            .GroupBy(a => a.UniqueId)
            .Select(a => a.First())
            .ToList();
    }

    private async Task InitializeFolderAsync()
    {
        if (SelectedFilterOption == null || SelectedFolderPivot == null || SelectedSortingOption == null)
            return;

        try
        {
            await MailCollection.ClearAsync();

            if (ActiveFolder == null)
                return;

            await ExecuteUIThread(() => { IsInitializingFolder = true; });

            // Folder is changed during initialization.
            // Just cancel the existing one and wait for new initialization.

            if (!listManipulationCancellationTokenSource.IsCancellationRequested)
            {
                listManipulationCancellationTokenSource.Cancel();
            }

            listManipulationCancellationTokenSource = new CancellationTokenSource();

            var cancellationToken = listManipulationCancellationTokenSource.Token;

            await listManipulationSemepahore.WaitAsync(cancellationToken);

            // Here items are sorted and filtered.

            List<MailCopy> items = null;
            List<MailCopy> onlineSearchItems = null;

            bool isDoingSearch = !string.IsNullOrEmpty(SearchQuery);
            bool isDoingOnlineSearch = false;

            if (isDoingSearch)
            {
                isDoingOnlineSearch = PreferencesService.DefaultSearchMode == SearchMode.Online || IsOnlineSearchEnabled;

                // Perform online search.
                if (isDoingOnlineSearch)
                {
                    try
                    {
                        onlineSearchItems = await PerformSynchronizerOnlineSearchAsync(SearchQuery, ActiveFolder.HandlingFolders, cancellationToken).ConfigureAwait(false);
                        await ExecuteUIThread(() => { AreSearchResultsOnline = true; });
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to perform online search.");

                        isDoingOnlineSearch = false;
                        onlineSearchItems = null;

                        await ExecuteUIThread(() =>
                        {
                            IsOnlineSearchEnabled = false;
                            AreSearchResultsOnline = false;

                            var serverErrorMessage = string.Format(Translator.OnlineSearchFailed_Message, ex.Message);
                            _mailDialogService.InfoBarMessage(Translator.GeneralTitle_Error, serverErrorMessage, InfoBarMessageType.Warning);
                        });
                    }
                }
            }

            var initializationOptions = new MailListInitializationOptions(ActiveFolder.HandlingFolders,
                                                                          SelectedFilterOption.Type,
                                                                          SelectedSortingOption.Type,
                                                                          PreferencesService.IsThreadingEnabled,
                                                                          SelectedFolderPivot.IsFocused,
                                                                          isDoingOnlineSearch ? string.Empty : SearchQuery,
                                                                          MailCollection.MailCopyIdHashSet,
                                                                          onlineSearchItems);

            items = await _mailService.FetchMailsAsync(initializationOptions, cancellationToken).ConfigureAwait(false);

            if (!listManipulationCancellationTokenSource.IsCancellationRequested)
            {
                // Here they are already threaded if needed.
                // We don't need to insert them one by one.
                // Just create VMs and do bulk insert.

                var viewModels = await PrepareMailViewModelsAsync(items, cancellationToken).ConfigureAwait(false);

                await MailCollection.AddRangeAsync(viewModels, clearIdCache: true);

                await ExecuteUIThread(() =>
                {
                    HasNoOnlineSearchResult = isDoingOnlineSearch && items.Count == 0;
                    OnPropertyChanged(nameof(HasNoOnlineSearchResult));

                    if (isDoingSearch && !isDoingOnlineSearch)
                    {
                        IsOnlineSearchButtonVisible = true;
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("Initialization of mails canceled.");
        }
        catch (Exception ex)
        {
            Debugger.Break();

            if (IsInSearchMode)
                Log.Error(ex, "Failed to perform search.");
            else
                Log.Error(ex, "Failed to refresh listed mails.");
        }
        finally
        {
            listManipulationSemepahore.Release();

            await ExecuteUIThread(() =>
            {
                IsInitializingFolder = false;

                OnPropertyChanged(nameof(CanSynchronize));
                NotifyItemFoundState();

                // Clear the loading message after completion
                IsBarOpen = false;
            });
        }
    }

    #region Receivers

    async void IRecipient<ActiveMailFolderChangedEvent>.Receive(ActiveMailFolderChangedEvent message)
    {
        NotifyItemSelected();

        isChangingFolder = true;

        ActiveFolder = message.BaseFolderMenuItem;
        gmailUnreadFolderMarkedAsReadUniqueIds.Clear();

        trackingSynchronizationId = null;
        completedTrackingSynchronizationCount = 0;

        // Notify change for archive-unarchive app bar button.
        OnPropertyChanged(nameof(IsArchiveSpecialFolder));

        IsInSearchMode = false;
        IsOnlineSearchButtonVisible = false;
        AreSearchResultsOnline = false;
        HasNoOnlineSearchResult = false;
        OnPropertyChanged(nameof(HasNoOnlineSearchResult));

        // Prepare Focused - Other or folder name tabs.
        await UpdateFolderPivotsAsync();

        // Reset filters and sorting options.
        ResetFilters();

        await InitializeFolderAsync();

        // TODO: This should be done in a better way.
        while (IsInitializingFolder)
        {
            await Task.Delay(100);
        }

        // Check whether the account synchronizer that this folder belongs to is already in synchronization.
        await CheckIfAccountIsSynchronizingAsync();

        // Let awaiters know about the completion of mail init.
        message.FolderInitLoadAwaitTask?.TrySetResult(true);

        await Task.Yield();

        isChangingFolder = false;

        void ResetFilters()
        {
            // Expected that FilterOptions and SortingOptions have default value in 0 index.
            SelectedFilterOption = FilterOptions[0];
            SelectedSortingOption = SortingOptions[0];
            SearchQuery = string.Empty;
            IsInSearchMode = false;
            IsOnlineSearchEnabled = false;
            HasNoOnlineSearchResult = false;
        }
    }

    public void Receive(AccountSynchronizationCompleted message)
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
                UpdateBarMessage(InfoBarMessageType.Success, ActiveFolder.FolderName, Translator.SynchronizationFolderReport_Success);
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
    }

    void IRecipient<MailItemNavigationRequested>.Receive(MailItemNavigationRequested message)
    {
        // TODO: Remove this.

        WeakReferenceMessenger.Default.Send(new SelectMailItemContainerEvent(message.UniqueMailId, message.ScrollToItem));
    }

    #endregion

    public async void Receive(NewMailSynchronizationRequested message)
        => await ExecuteUIThread(() => { OnPropertyChanged(nameof(CanSynchronize)); });

    protected override async void OnFolderSynchronizationEnabled(IMailItemFolder mailItemFolder)
    {
        if (ActiveFolder?.EntityId != mailItemFolder.Id) return;

        await ExecuteUIThread(() =>
        {
            ActiveFolder.UpdateFolder(mailItemFolder);

            OnPropertyChanged(nameof(CanSynchronize));
            OnPropertyChanged(nameof(IsFolderSynchronizationEnabled));
        });

        SyncFolderCommand?.Execute(null);
    }

    public async void Receive(AccountSynchronizerStateChanged message)
        => await CheckIfAccountIsSynchronizingAsync();

    private async Task CheckIfAccountIsSynchronizingAsync()
    {
        bool isAnyAccountSynchronizing = false;

        // Check each account that this page is listing folders from.
        // If any of the synchronizers are synchronizing, we disable sync.

        if (ActiveFolder != null)
        {
            var accountIds = ActiveFolder.HandlingFolders.Select(a => a.MailAccountId);

            foreach (var accountId in accountIds)
            {
                if (SynchronizationManager.Instance.IsAccountSynchronizing(accountId))
                {
                    isAnyAccountSynchronizing = true;
                    break;
                }
            }
        }

        await ExecuteUIThread(() => { IsAccountSynchronizerInSynchronization = isAnyAccountSynchronizing; });
    }

    public async void Receive(AccountCacheResetMessage message)
    {
        if (message.Reason == AccountCacheResetReason.ExpiredCache &&
            ActiveFolder.HandlingFolders.Any(a => a.MailAccountId == message.AccountId))
        {
            var handlingFolder = ActiveFolder.HandlingFolders.FirstOrDefault(a => a.MailAccountId == message.AccountId);

            if (handlingFolder == null) return;

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
        Messenger.Register<PropertyChangedMessage<bool>>(this);
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
        Messenger.Unregister<PropertyChangedMessage<bool>>(this);
    }

    public void Receive(PropertyChangedMessage<bool> message)
    {
        // Handle IsSelected property changes from MailItemViewModel
        if (message.PropertyName == nameof(MailItemViewModel.IsSelected) && message.Sender is MailItemViewModel mailItemViewModel)
        {
            Messenger.Send(new SelectedItemsChangedMessage());
        }
        else if (message.Sender is ThreadMailItemViewModel threadMailItemViewModel)
        {
            if (message.PropertyName == nameof(ThreadMailItemViewModel.IsSelected))
            {
                // Thread selected.
            }
            else if (message.PropertyName == nameof(ThreadMailItemViewModel.IsThreadExpanded))
            {
                // Thread expanded.
            }
        }
    }

    public async void Receive(SwipeActionRequested message)
    {
        if (message.MailItem == null) return;

        // Get mail copies based on the mail item type
        IEnumerable<MailCopy> mailCopies;

        if (message.MailItem is MailItemViewModel singleItem)
        {
            mailCopies = new[] { singleItem.MailCopy };
        }
        else if (message.MailItem is ThreadMailItemViewModel threadItem)
        {
            mailCopies = threadItem.ThreadEmails.Select(e => e.MailCopy);
        }
        else
        {
            return; // Unknown mail item type
        }

        var package = new MailOperationPreperationRequest(message.Operation, mailCopies);
        await ExecuteMailOperationAsync(package);
    }
}
