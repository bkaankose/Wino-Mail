using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Folders;
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
    public ObservableCollection<IMenuOperation> ActionItems { get; set; } = [];

    private readonly SemaphoreSlim listManipulationSemepahore = new SemaphoreSlim(1);
    private CancellationTokenSource listManipulationCancellationTokenSource = new CancellationTokenSource();

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
    [NotifyPropertyChangedFor(nameof(IsCategoryView))]
    [NotifyPropertyChangedFor(nameof(IsSyncButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsJunkFolder))]
    [NotifyPropertyChangedFor(nameof(IsEmptyFolderButtonVisible))]
    [NotifyCanExecuteChangedFor(nameof(EmptyFolderCommand))]
    public partial IBaseFolderMenuItem ActiveFolder { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize))]
    [NotifyCanExecuteChangedFor(nameof(EmptyFolderCommand))]
    public partial bool IsAccountSynchronizerInSynchronization { get; set; }

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
                                 IWinoLogger winoLogger)
    {
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

        PreferencesService = preferencesService;
        ThemeService = themeService;
        StatePersistenceService = statePersistenceService;
        _notificationBuilder = notificationBuilder;
        NavigationService = navigationService;

        SelectedFilterOption = FilterOptions[0];
        SelectedSortingOption = SortingOptions[0];
        MailCollection.ThreadItemFactory = threadId => new ThreadMailItemViewModel(threadId, PreferencesService.IsNewestThreadMailFirst);

        MailListLength = statePersistenceService.MailListPaneLength;
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

    public bool CanSynchronize => !IsCategoryView && !IsAccountSynchronizerInSynchronization && IsFolderSynchronizationEnabled;
    public bool IsFolderSynchronizationEnabled => ActiveFolder?.IsSynchronizationEnabled ?? false;
    public bool IsArchiveSpecialFolder => ActiveFolder?.SpecialFolderType == SpecialFolderType.Archive;
    public bool IsJunkFolder => ActiveFolder?.SpecialFolderType == SpecialFolderType.Junk;
    public bool IsCategoryView => ActiveFolder is IMailCategoryMenuItem or IMergedMailCategoryMenuItem;
    public bool IsSyncButtonVisible => !IsCategoryView;
    public bool IsEmptyFolderButtonVisible => IsJunkFolder;

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
            if (IsCategoryView)
            {
                PivotFolders.Add(new FolderPivotViewModel(ActiveFolder.FolderName, null));
            }
            // Merged folders don't support focused feature.
            else if (ActiveFolder is IMergedAccountFolderMenuItem)
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
    private async Task ExecuteTopBarAction(IMenuOperation menuItem)
    {
        if (menuItem is not MailOperationMenuItem mailOperationMenuItem || MailCollection.SelectedItemsCount == 0) return;

        await HandleMailOperation(mailOperationMenuItem.Operation, MailCollection.SelectedItems);
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

    private bool CanEmptyFolder() => IsJunkFolder && !IsAccountSynchronizerInSynchronization;

    [RelayCommand(CanExecute = nameof(CanLoadMoreItems))]
    private async Task LoadMoreItemsAsync()
    {
        if (IsInitializingFolder || IsOnlineSearchEnabled || FinishedLoading) return;

        Debug.WriteLine("Loading more...");
        await ExecuteUIThread(() => { IsInitializingFolder = true; });

        var initializationOptions = CreateInitializationOptions(
            IsInSearchMode ? SearchQuery : string.Empty,
            MailCollection.MailCopyIdHashSet);

        var items = await _mailService.FetchMailsAsync(initializationOptions).ConfigureAwait(false);

        if (items.Count == 0)
        {
            await ExecuteUIThread(() => { FinishedLoading = true; });

            return;
        }

        var viewModels = await PrepareMailViewModelsAsync(items).ConfigureAwait(false);
        var pendingOperationUniqueIds = await GetPendingOperationUniqueIdsForActiveFolderAccountsAsync().ConfigureAwait(false);
        ApplyPendingOperationBusyStates(viewModels, pendingOperationUniqueIds);

        await MailCollection.AddRangeAsync(viewModels, false);
        await ExecuteUIThread(() => { IsInitializingFolder = false; });
    }

    #endregion

    public Task ExecuteMailOperationAsync(MailOperationPreperationRequest package) => _winoRequestDelegator.ExecuteAsync(package);

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
                await CreateReplyDraftAsync(DraftCreationReason.Reply);
                args.Handled = true;
                break;
            case KeyboardShortcutAction.ReplyAll:
                await CreateReplyDraftAsync(DraftCreationReason.ReplyAll);
                args.Handled = true;
                break;
        }
    }

    private IEnumerable<MailItemViewModel> GetShortcutTargetItems()
    {
        if (MailCollection.SelectedItemsCount > 0)
            return MailCollection.SelectedItems.OfType<MailItemViewModel>();

        if (_activeMailItem != null)
            return [_activeMailItem];

        return [];
    }

    private async Task CreateReplyDraftAsync(DraftCreationReason reason)
    {
        var targetMail = GetShortcutTargetItems().FirstOrDefault();
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

        var (draftMailCopy, draftBase64MimeMessage) = await _mailService.CreateDraftAsync(targetMail.MailCopy.AssignedAccount.Id, draftOptions).ConfigureAwait(false);
        var draftPreparationRequest = new DraftPreparationRequest(targetMail.MailCopy.AssignedAccount, draftMailCopy, draftBase64MimeMessage, draftOptions.Reason, targetMail.MailCopy);
        await _winoRequestDelegator.ExecuteAsync(draftPreparationRequest);
    }

    public IEnumerable<MailOperationMenuItem> GetAvailableMailActions(IEnumerable<MailItemViewModel> contextMailItems)
        => _contextMenuItemService.GetMailItemContextMenuActions(contextMailItems.Select(a => a.MailCopy));

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
        bool condition = mailItem.IsRead
                          && SelectedFilterOption.Type == FilterOptionType.Unread
                          || !mailItem.IsFlagged
                          && SelectedFilterOption.Type == FilterOptionType.Flagged;

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

        // 2) Include items that belong to the active folder.
        if (BelongsToActiveFolder(addedMail))
            return true;

        // 3) Draft-specific visibility: include drafts while viewing Drafts.
        if (addedMail.IsDraft && IsActiveDraftFolder())
            return true;

        return false;
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

    private bool ShouldRemoveUpdatedMailFromCurrentList(MailCopy updatedMail)
    {
        // Update flow already checks if this item is currently listed.
        // Keep the item in the list and update in-place.
        _ = updatedMail;
        return false;
    }

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

        bool hasLock = false;

        try
        {
            await listManipulationSemepahore.WaitAsync();
            hasLock = true;

            if (ActiveFolder == null) return;

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

            if (!ShouldIncludeAddedMailInCurrentList(addedMail)) return;
            if (ShouldPreventItemAdd(addedMail)) return;

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

            // AddAsync already handles UI threading internally, no need to wrap it
            await MailCollection.AddAsync(addedMail);

            if (source == EntityUpdateSource.ClientUpdated)
            {
                var addedContainer = MailCollection.GetMailItemContainer(addedMail.UniqueId);
                if (addedContainer?.ItemViewModel != null)
                {
                    addedContainer.ItemViewModel.IsBusy = true;
                }
            }

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
            });
        }
        catch { }
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

            await MailCollection.UpdateMailCopy(updatedMail, source, changedProperties);
        }
        finally
        {
            listManipulationSemepahore.Release();
        }

        await ExecuteUIThread(() => { SetupTopBarActions(); });
    }

    protected override async void OnMailStateUpdated(MailStateChange updatedState, EntityUpdateSource source)
    {
        base.OnMailStateUpdated(updatedState, source);

        if (updatedState == null)
            return;

        try
        {
            await listManipulationSemepahore.WaitAsync();

            if (!MailCollection.ContainsMailUniqueId(updatedState.UniqueId))
                return;

            await MailCollection.UpdateMailStateAsync(updatedState, source);
        }
        finally
        {
            listManipulationSemepahore.Release();
        }

        await ExecuteUIThread(() => { SetupTopBarActions(); });
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

        try
        {
            await listManipulationSemepahore.WaitAsync();

            var listedStates = targetStates
                .Where(state => MailCollection.ContainsMailUniqueId(state.UniqueId))
                .ToList();

            if (listedStates.Count == 0)
                return;

            await MailCollection.UpdateMailStatesAsync(listedStates, source);
        }
        finally
        {
            listManipulationSemepahore.Release();
        }

        await ExecuteUIThread(() => { SetupTopBarActions(); });
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

        try
        {
            await listManipulationSemepahore.WaitAsync();

            var listedMails = targetMails
                .Where(mail => MailCollection.ContainsMailUniqueId(mail.UniqueId))
                .ToList();

            if (listedMails.Count == 0)
                return;

            var mailsToRemove = listedMails
                .Where(ShouldRemoveUpdatedMailFromCurrentList)
                .ToList();

            var mailIdsToRemove = mailsToRemove.Select(x => x.UniqueId).ToHashSet();
            var mailsToUpdate = listedMails
                .Where(mail => !mailIdsToRemove.Contains(mail.UniqueId))
                .ToList();

            if (mailsToRemove.Count > 0)
            {
                await MailCollection.RemoveRangeAsync(mailsToRemove);
            }

            if (mailsToUpdate.Count > 0)
            {
                await MailCollection.UpdateMailCopiesAsync(mailsToUpdate, source, changedProperties);
            }

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
                SetupTopBarActions();
            });
        }
        finally
        {
            listManipulationSemepahore.Release();
        }
    }

    protected override async void OnMailRemoved(MailCopy removedMail, EntityUpdateSource source)
    {
        base.OnMailRemoved(removedMail, source);

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
                MailItemViewModel nextItem = null;
                bool isDeletedMailSelected = false;

                await ExecuteUIThread(() =>
                {
                    isDeletedMailSelected = MailCollection.SelectedItems.Any(a => a.MailCopy.UniqueId == removedMail.UniqueId);

                    if (isDeletedMailSelected && PreferencesService.AutoSelectNextItem)
                    {
                        nextItem = MailCollection.GetNextItem(removedMail);
                    }
                });

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

    protected override async void OnBulkMailRemoved(IReadOnlyList<MailCopy> removedMails, EntityUpdateSource source)
    {
        var targetMails = removedMails?
            .Where(x => x != null && x.AssignedAccount != null)
            .GroupBy(x => x.UniqueId)
            .Select(group => group.First())
            .ToList() ?? [];

        if (targetMails.Count == 0)
            return;

        try
        {
            await listManipulationSemepahore.WaitAsync();

            var existingMails = targetMails
                .Where(mail => MailCollection.ContainsMailUniqueId(mail.UniqueId))
                .ToList();

            if (existingMails.Count == 0)
                return;

            var removedMailIds = existingMails.Select(mail => mail.UniqueId).ToHashSet();
            var shouldClearSelection = false;

            await ExecuteUIThread(() =>
            {
                shouldClearSelection = MailCollection.SelectedItems.Any(item => removedMailIds.Contains(item.MailCopy.UniqueId));
            });

            await MailCollection.RemoveRangeAsync(existingMails);

            if (shouldClearSelection)
            {
                await MailCollection.UnselectAllAsync();
            }

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
                SetupTopBarActions();
            });
        }
        finally
        {
            listManipulationSemepahore.Release();
        }
    }

    protected override async void OnBulkMailAdded(IReadOnlyList<MailCopy> addedMails, EntityUpdateSource source)
    {
        var targetMails = addedMails?
            .Where(x => x != null)
            .GroupBy(x => x.UniqueId)
            .Select(group => group.First())
            .ToList() ?? [];

        if (targetMails.Count == 0)
            return;

        try
        {
            await listManipulationSemepahore.WaitAsync();

            var mailsToAdd = new List<MailCopy>();

            foreach (var addedMail in targetMails)
            {
                if (MailCollection.ContainsMailUniqueId(addedMail.UniqueId))
                    continue;

                if (!ShouldIncludeAddedMailInCurrentList(addedMail))
                    continue;

                if (ShouldPreventItemAdd(addedMail))
                    continue;

                if (SelectedFolderPivot?.IsFocused is bool isFocused && addedMail.IsFocused != isFocused)
                    continue;

                if (IsInSearchMode)
                {
                    if (IsOnlineSearchEnabled || AreSearchResultsOnline)
                        continue;

                    if (!IsMailMatchingLocalSearch(addedMail))
                        continue;
                }

                mailsToAdd.Add(addedMail);
            }

            if (mailsToAdd.Count == 0)
                return;

            await MailCollection.AddRangeAsync(mailsToAdd.Select(mail => new MailItemViewModel(mail)), false);

            await ExecuteUIThread(() =>
            {
                NotifyItemFoundState();
                SetupTopBarActions();
            });
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
        await PopulateMailCategoriesAsync(mailItems, cancellationToken).ConfigureAwait(false);

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

    private async Task PopulateMailCategoriesAsync(IEnumerable<MailCopy> mailItems, CancellationToken cancellationToken)
    {
        var mails = mailItems?.Where(a => a != null).ToList() ?? [];
        if (mails.Count == 0)
            return;

        var accountIdsByFolderId = ActiveFolder?.HandlingFolders?
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

    private async Task<HashSet<Guid>> GetPendingOperationUniqueIdsForActiveFolderAccountsAsync(CancellationToken cancellationToken = default)
    {
        var pendingOperationUniqueIds = new HashSet<Guid>();

        var accountIds = ActiveFolder?.HandlingFolders?
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

    private MailListInitializationOptions CreateInitializationOptions(
        string searchQuery,
        System.Collections.Concurrent.ConcurrentDictionary<Guid, bool> existingUniqueIds,
        List<MailCopy> preFetchedMailCopies = null,
        bool deduplicateByServerId = false)
    {
        var options = new MailListInitializationOptions(ActiveFolder.HandlingFolders,
                                                        SelectedFilterOption.Type,
                                                        SelectedSortingOption.Type,
                                                        PreferencesService.IsThreadingEnabled,
                                                        SelectedFolderPivot.IsFocused,
                                                        searchQuery,
                                                        existingUniqueIds,
                                                        preFetchedMailCopies,
                                                        DeduplicateByServerId: deduplicateByServerId);

        if (!IsCategoryView)
            return options;

        var categoryIds = ActiveFolder switch
        {
            IMailCategoryMenuItem singleCategoryMenuItem => new List<Guid> { singleCategoryMenuItem.MailCategory.Id },
            IMergedMailCategoryMenuItem mergedCategoryMenuItem => mergedCategoryMenuItem.Categories.Select(a => a.Id).ToList(),
            _ => []
        };

        return options with
        {
            CategoryIds = categoryIds
        };
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
            var synchronizer = await SynchronizationManager.Instance.GetSynchronizerAsync(groupedFolders.Key).ConfigureAwait(false);
            if (synchronizer == null) return new List<MailCopy>();

            var accountResults = await synchronizer.OnlineSearchAsync(queryText, groupedFolders.ToList(), cancellationToken).ConfigureAwait(false);
            return accountResults ?? new List<MailCopy>();
        });

        var allResults = await Task.WhenAll(searchTasks).ConfigureAwait(false);

        var accountIdsByFolderId = distinctFolders.ToDictionary(folder => folder.Id, folder => folder.MailAccountId);
        var preferredFolderIds = distinctFolders.Select(folder => folder.Id).ToHashSet();

        return DeduplicateOnlineSearchResults(allResults.SelectMany(a => a), accountIdsByFolderId, preferredFolderIds);
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

    private async Task InitializeFolderAsync()
    {
        if (SelectedFilterOption == null || SelectedFolderPivot == null || SelectedSortingOption == null)
            return;

        try
        {
            await MailCollection.ClearAsync();

            if (ActiveFolder == null)
                return;

            MailCollection.PruneSingleNonDraftItems = IsActiveDraftFolder();

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

            var initialExistingIds = new ConcurrentDictionary<Guid, bool>(MailCollection.MailCopyIdHashSet);
            var localPinnedItems = new List<MailCopy>();

            if (!isDoingOnlineSearch)
            {
                var pinnedOptions = CreateInitializationOptions(SearchQuery, MailCollection.MailCopyIdHashSet);
                localPinnedItems = await _mailService.FetchPinnedMailsAsync(pinnedOptions, cancellationToken).ConfigureAwait(false);

                foreach (var pinnedItem in localPinnedItems)
                {
                    initialExistingIds.TryAdd(pinnedItem.UniqueId, true);
                }
            }

            var initializationOptions = CreateInitializationOptions(
                isDoingOnlineSearch ? string.Empty : SearchQuery,
                initialExistingIds,
                onlineSearchItems,
                isDoingOnlineSearch);

            items = await _mailService.FetchMailsAsync(initializationOptions, cancellationToken).ConfigureAwait(false);
            items = localPinnedItems.Count > 0 ? [.. localPinnedItems, .. items] : items;

            if (!listManipulationCancellationTokenSource.IsCancellationRequested)
            {
                // Here they are already threaded if needed.
                // We don't need to insert them one by one.
                // Just create VMs and do bulk insert.

                var viewModels = await PrepareMailViewModelsAsync(items, cancellationToken).ConfigureAwait(false);
                var pendingOperationUniqueIds = await GetPendingOperationUniqueIdsForActiveFolderAccountsAsync(cancellationToken).ConfigureAwait(false);
                ApplyPendingOperationBusyStates(viewModels, pendingOperationUniqueIds);

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
