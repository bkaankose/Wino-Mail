using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.AppCenter.Crashes;
using MoreLinq;
using Nito.AsyncEx;
using Serilog;
using Wino.Core;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels
{
    public partial class MailListPageViewModel : BaseViewModel,
        IRecipient<MailItemNavigationRequested>,
        IRecipient<ActiveMailFolderChangedEvent>,
        IRecipient<MailItemSelectedEvent>,
        IRecipient<MailItemSelectionRemovedEvent>,
        IRecipient<AccountSynchronizationCompleted>,
        IRecipient<NewSynchronizationRequested>,
        IRecipient<AccountSynchronizerStateChanged>,
        IRecipient<SelectedMailItemsChanged>
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

        private IObservable<System.Reactive.EventPattern<NotifyCollectionChangedEventArgs>> selectionChangedObservable = null;

        public WinoMailCollection MailCollection { get; } = new WinoMailCollection();

        public ObservableCollection<MailItemViewModel> SelectedItems { get; set; } = [];
        public ObservableCollection<FolderPivotViewModel> PivotFolders { get; set; } = [];

        private readonly SemaphoreSlim listManipulationSemepahore = new SemaphoreSlim(1);
        private CancellationTokenSource listManipulationCancellationTokenSource = new CancellationTokenSource();

        public IWinoNavigationService NavigationService { get; }
        public IStatePersistanceService StatePersistanceService { get; }
        public IPreferencesService PreferencesService { get; }

        private readonly IMailService _mailService;
        private readonly IFolderService _folderService;
        private readonly IThreadingStrategyProvider _threadingStrategyProvider;
        private readonly IContextMenuItemService _contextMenuItemService;
        private readonly IWinoRequestDelegator _winoRequestDelegator;
        private readonly IKeyPressService _keyPressService;
        private readonly IWinoServerConnectionManager _winoServerConnectionManager;
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
        private string searchQuery;

        [ObservableProperty]
        private FilterOption _selectedFilterOption;
        private SortingOption _selectedSortingOption;

        // Indicates state when folder is initializing. It can happen after folder navigation, search or filter change applied or loading more items.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        [NotifyPropertyChangedFor(nameof(IsCriteriaFailed))]
        [NotifyPropertyChangedFor(nameof(IsFolderEmpty))]
        [NotifyPropertyChangedFor(nameof(IsProgressRing))]
        private bool isInitializingFolder;

        [ObservableProperty]
        private InfoBarMessageType barSeverity;

        [ObservableProperty]
        private string barMessage;

        [ObservableProperty]
        private string barTitle;

        [ObservableProperty]
        private bool isBarOpen;

        /// <summary>
        /// Current folder that is being represented from the menu.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSynchronize))]
        [NotifyPropertyChangedFor(nameof(IsFolderSynchronizationEnabled))]
        private IBaseFolderMenuItem activeFolder;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSynchronize))]
        private bool isAccountSynchronizerInSynchronization;

        public MailListPageViewModel(IDialogService dialogService,
                                     IWinoNavigationService navigationService,
                                     IMailService mailService,
                                     IStatePersistanceService statePersistanceService,
                                     IFolderService folderService,
                                     IThreadingStrategyProvider threadingStrategyProvider,
                                     IContextMenuItemService contextMenuItemService,
                                     IWinoRequestDelegator winoRequestDelegator,
                                     IKeyPressService keyPressService,
                                     IPreferencesService preferencesService,
                                     IWinoServerConnectionManager winoServerConnectionManager) : base(dialogService)
        {
            PreferencesService = preferencesService;
            _winoServerConnectionManager = winoServerConnectionManager;
            StatePersistanceService = statePersistanceService;
            NavigationService = navigationService;

            _mailService = mailService;
            _folderService = folderService;
            _threadingStrategyProvider = threadingStrategyProvider;
            _contextMenuItemService = contextMenuItemService;
            _winoRequestDelegator = winoRequestDelegator;
            _keyPressService = keyPressService;

            SelectedFilterOption = FilterOptions[0];
            SelectedSortingOption = SortingOptions[0];

            selectionChangedObservable = Observable.FromEventPattern<NotifyCollectionChangedEventArgs>(SelectedItems, nameof(SelectedItems.CollectionChanged));
            selectionChangedObservable
                .Throttle(TimeSpan.FromMilliseconds(100))
                .Subscribe(async a =>
                {
                    await ExecuteUIThread(() => { SelectedItemCollectionUpdated(a.EventArgs); });
                });

            MailCollection.MailItemRemoved += (c, removedItem) =>
            {
                if (removedItem is ThreadMailItemViewModel removedThreadViewModelItem)
                {
                    foreach (var viewModel in removedThreadViewModelItem.ThreadItems.Cast<MailItemViewModel>())
                    {
                        if (SelectedItems.Contains(viewModel))
                        {
                            SelectedItems.Remove(viewModel);
                        }
                    }
                }
                else if (removedItem is MailItemViewModel removedMailItemViewModel && SelectedItems.Contains(removedMailItemViewModel))
                {
                    SelectedItems.Remove(removedMailItemViewModel);
                }
            };
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
                        MailCollection.SortingType = value.Type;
                    }
                }
            }
        }

        public bool CanSynchronize => !IsAccountSynchronizerInSynchronization && IsFolderSynchronizationEnabled;
        public bool IsFolderSynchronizationEnabled => ActiveFolder?.IsSynchronizationEnabled ?? false;
        public int SelectedItemCount => SelectedItems.Count;
        public bool HasMultipleItemSelections => SelectedItemCount > 1;
        public bool HasSingleItemSelection => SelectedItemCount == 1;
        public bool HasSelectedItems => SelectedItems.Any();
        public bool IsArchiveSpecialFolder => ActiveFolder?.SpecialFolderType == SpecialFolderType.Archive;

        public string SelectedMessageText => HasSelectedItems ? string.Format(Translator.MailsSelected, SelectedItemCount) : Translator.NoMailSelected;
        /// <summary>
        /// Indicates current state of the mail list. Doesn't matter it's loading or no.
        /// </summary>
        public bool IsEmpty => MailCollection.Count == 0;

        /// <summary>
        /// Progress ring only should be visible when the folder is initializing and there are no items. We don't need to show it when there are items.
        /// </summary>
        public bool IsProgressRing => IsInitializingFolder && IsEmpty;
        private bool isFilters => IsInSearchMode || SelectedFilterOption.Type != FilterOptionType.All;
        public bool IsCriteriaFailed => !IsInitializingFolder && IsEmpty && isFilters;
        public bool IsFolderEmpty => !IsInitializingFolder && IsEmpty && !isFilters;

        public bool IsInSearchMode { get; set; }

        #endregion

        private async void ActiveMailItemChanged(MailItemViewModel selectedMailItemViewModel)
        {
            if (_activeMailItem == selectedMailItemViewModel) return;

            // Don't update active mail item if Ctrl key is pressed or multi selection is ennabled.
            // User is probably trying to select multiple items.
            // This is not the same behavior in Windows Mail,
            // but it's a trash behavior.

            var isCtrlKeyPressed = _keyPressService.IsCtrlKeyPressed();

            bool isMultiSelecting = isCtrlKeyPressed || IsMultiSelectionModeEnabled;

            if (isMultiSelecting ? StatePersistanceService.IsReaderNarrowed : false)
            {
                // Don't change the active mail item if the reader is narrowed, but just update the shell.
                Messenger.Send(new ShellStateUpdated());
                return;
            }

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
            OnPropertyChanged(nameof(HasSingleItemSelection));
            OnPropertyChanged(nameof(HasSelectedItems));
            OnPropertyChanged(nameof(SelectedItemCount));
            OnPropertyChanged(nameof(HasMultipleItemSelections));

            if (SelectedFolderPivot != null)
                SelectedFolderPivot.SelectedItemCount = SelectedItemCount;
        }

        private void NotifyItemFoundState()
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsCriteriaFailed));
            OnPropertyChanged(nameof(IsFolderEmpty));
        }

        protected override void OnDispatcherAssigned()
        {
            base.OnDispatcherAssigned();

            MailCollection.CoreDispatcher = Dispatcher;
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

        private void SelectedItemCollectionUpdated(NotifyCollectionChangedEventArgs e)
        {
            if (SelectedItems.Count == 1)
            {
                ActiveMailItemChanged(SelectedItems[0]);
            }
            else
            {
                // At this point, either we don't have any item selected 
                // or we have multiple item selected. In either case
                // there should be no active item.

                ActiveMailItemChanged(null);
            }

            NotifyItemSelected();

            Messenger.Send(new SelectedMailItemsChanged(SelectedItems.Count));
        }

        private void UpdateFolderPivots()
        {
            PivotFolders.Clear();
            SelectedFolderPivot = null;

            if (ActiveFolder == null) return;

            // Merged folders don't support focused feature.

            if (ActiveFolder is IMergedAccountFolderMenuItem)
            {
                PivotFolders.Add(new FolderPivotViewModel(ActiveFolder.FolderName, null));
            }
            else if (ActiveFolder is IFolderMenuItem singleFolderMenuItem)
            {
                var parentAccount = singleFolderMenuItem.ParentAccount;

                bool isAccountSupportsFocusedInbox = parentAccount.Preferences.IsFocusedInboxEnabled != null;
                bool isFocusedInboxEnabled = isAccountSupportsFocusedInbox && parentAccount.Preferences.IsFocusedInboxEnabled.GetValueOrDefault();
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

            // This will trigger refresh.
            SelectedFolderPivot = PivotFolders.FirstOrDefault();
        }

        #region Commands

        [RelayCommand]
        public Task ExecuteHoverAction(MailOperationPreperationRequest request) => ExecuteMailOperationAsync(request);

        /// <summary>
        /// Executes the requested mail operation for currently selected items.
        /// </summary>
        /// <param name="operation">Action to execute for selected items.</param>
        [RelayCommand]
        private async Task MailOperationAsync(int mailOperationIndex)
        {
            if (!SelectedItems.Any()) return;

            // Commands don't like enums. So it has to be int.
            var operation = (MailOperation)mailOperationIndex;

            var package = new MailOperationPreperationRequest(operation, SelectedItems.Select(a => a.MailCopy));

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
                var options = new SynchronizationOptions()
                {
                    AccountId = folder.MailAccountId,
                    Type = SynchronizationType.Custom,
                    SynchronizationFolderIds = [folder.Id],
                    GroupedSynchronizationTrackingId = trackingSynchronizationId
                };

                Messenger.Send(new NewSynchronizationRequested(options, SynchronizationSource.Client));
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
            if (string.IsNullOrEmpty(SearchQuery) && IsInSearchMode)
            {
                UpdateFolderPivots();
                IsInSearchMode = false;
                await InitializeFolderAsync();
            }

            if (!string.IsNullOrEmpty(SearchQuery))
            {

                IsInSearchMode = true;
                CreateSearchPivot();
            }

            void CreateSearchPivot()
            {
                PivotFolders.Clear();
                var isFocused = SelectedFolderPivot?.IsFocused;
                SelectedFolderPivot = null;

                if (ActiveFolder == null) return;

                PivotFolders.Add(new FolderPivotViewModel(Translator.SearchPivotName, isFocused));

                // This will trigger refresh.
                SelectedFolderPivot = PivotFolders.FirstOrDefault();
            }
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

        [RelayCommand]
        private async Task LoadMoreItemsAsync()
        {
            if (IsInitializingFolder) return;

            await ExecuteUIThread(() => { IsInitializingFolder = true; });

            var initializationOptions = new MailListInitializationOptions(ActiveFolder.HandlingFolders,
                                                                          SelectedFilterOption.Type,
                                                                          SelectedSortingOption.Type,
                                                                          PreferencesService.IsThreadingEnabled,
                                                                          SelectedFolderPivot.IsFocused,
                                                                          IsInSearchMode ? SearchQuery : string.Empty,
                                                                          MailCollection.MailCopyIdHashSet);

            var items = await _mailService.FetchMailsAsync(initializationOptions).ConfigureAwait(false);

            var viewModels = PrepareMailViewModels(items);

            await ExecuteUIThread(() => { MailCollection.AddRange(viewModels, clearIdCache: false); });
            await ExecuteUIThread(() => { IsInitializingFolder = false; });
        }

        #endregion

        public Task ExecuteMailOperationAsync(MailOperationPreperationRequest package) => _winoRequestDelegator.ExecuteAsync(package);

        public IEnumerable<MailItemViewModel> GetTargetMailItemViewModels(IMailItem clickedItem)
        {
            // Threat threads as a whole and include everything in the group. Except single selections outside of the thread.
            IEnumerable<MailItemViewModel> contextMailItems = null;

            if (clickedItem is ThreadMailItemViewModel clickedThreadItem)
            {
                // Clicked item is a thread.

                clickedThreadItem.IsThreadExpanded = true;
                contextMailItems = clickedThreadItem.ThreadItems.Cast<MailItemViewModel>();

                // contextMailItems = clickedThreadItem.GetMailCopies();
            }
            else if (clickedItem is MailItemViewModel clickedMailItemViewModel)
            {
                // If the clicked item is included in SelectedItems, then we need to thing them as whole.
                // If there are selected items, but clicked item is not one of them, then it's a single context menu.

                bool includedInSelectedItems = SelectedItems.Contains(clickedItem);

                if (includedInSelectedItems)
                    contextMailItems = SelectedItems;
                else
                    contextMailItems = [clickedMailItemViewModel];
            }

            return contextMailItems;
        }

        public IEnumerable<MailOperationMenuItem> GetAvailableMailActions(IEnumerable<IMailItem> contextMailItems)
            => _contextMenuItemService.GetMailItemContextMenuActions(contextMailItems);

        public void ChangeCustomFocusedState(IEnumerable<IMailItem> mailItems, bool isFocused)
            => mailItems.OfType<MailItemViewModel>().ForEach(a => a.IsCustomFocused = isFocused);

        private bool ShouldPreventItemAdd(IMailItem mailItem)
        {
            bool condition = mailItem.IsRead
                              && SelectedFilterOption.Type == FilterOptionType.Unread
                              || !mailItem.IsFlagged
                              && SelectedFilterOption.Type == FilterOptionType.Flagged;

            return condition;
        }

        protected override async void OnMailAdded(MailCopy addedMail)
        {
            base.OnMailAdded(addedMail);

            if (addedMail.AssignedAccount == null || addedMail.AssignedFolder == null) return;

            try
            {
                await listManipulationSemepahore.WaitAsync();

                if (ActiveFolder == null) return;

                // Messages coming to sent or draft folder must be inserted regardless of the filter.
                bool shouldPreventIgnoringFilter = addedMail.AssignedFolder.SpecialFolderType == SpecialFolderType.Draft ||
                                                   addedMail.AssignedFolder.SpecialFolderType == SpecialFolderType.Sent;

                // Item does not belong to this folder and doesn't have special type to be inserted.
                if (!shouldPreventIgnoringFilter && !ActiveFolder.HandlingFolders.Any(a => a.Id == addedMail.AssignedFolder.Id)) return;

                if (!shouldPreventIgnoringFilter && ShouldPreventItemAdd(addedMail)) return;

                await MailCollection.AddAsync(addedMail);

                await ExecuteUIThread(() => { NotifyItemFoundState(); });
            }
            catch { }
            finally
            {
                listManipulationSemepahore.Release();
            }
        }

        protected override async void OnMailUpdated(MailCopy updatedMail)
        {
            base.OnMailUpdated(updatedMail);

            Debug.WriteLine($"Updating {updatedMail.Id}-> {updatedMail.UniqueId}");

            await MailCollection.UpdateMailCopy(updatedMail);
        }

        protected override async void OnMailRemoved(MailCopy removedMail)
        {
            base.OnMailRemoved(removedMail);

            if (removedMail.AssignedAccount == null || removedMail.AssignedFolder == null) return;

            // We should delete the items only if:
            // 1. They are deleted from the active folder.
            // 2. Deleted from draft or sent folder.
            // 3. Removal is not caused by Gmail Unread folder action.
            // Delete/sent are special folders that can list their items in other folders.

            bool removedFromActiveFolder = ActiveFolder.HandlingFolders.Any(a => a.Id == removedMail.AssignedFolder.Id);
            bool removedFromDraftOrSent = removedMail.AssignedFolder.SpecialFolderType == SpecialFolderType.Draft ||
                                          removedMail.AssignedFolder.SpecialFolderType == SpecialFolderType.Sent;

            bool isDeletedByGmailUnreadFolderAction = ActiveFolder.SpecialFolderType == SpecialFolderType.Unread &&
                                                      gmailUnreadFolderMarkedAsReadUniqueIds.Contains(removedMail.UniqueId);

            if ((removedFromActiveFolder || removedFromDraftOrSent) && !isDeletedByGmailUnreadFolderAction)
            {
                bool isDeletedMailSelected = SelectedItems.Any(a => a.MailCopy.UniqueId == removedMail.UniqueId);

                // Automatically select the next item in the list if the setting is enabled.
                MailItemViewModel nextItem = null;

                if (isDeletedMailSelected && PreferencesService.AutoSelectNextItem)
                {
                    nextItem = MailCollection.GetNextItem(removedMail);
                }

                // Remove the deleted item from the list.
                await MailCollection.RemoveAsync(removedMail);

                if (nextItem != null)
                    WeakReferenceMessenger.Default.Send(new SelectMailItemContainerEvent(nextItem, ScrollToItem: true));
                else if (isDeletedMailSelected)
                {
                    // There are no next item to select, but we removed the last item which was selected.
                    // Clearing selected item will dispose rendering page.

                    SelectedItems.Clear();
                }

                await ExecuteUIThread(() => { NotifyItemFoundState(); });
            }
            else if (isDeletedByGmailUnreadFolderAction)
            {
                // Remove the entry from the set so we can listen to actual deletes next time.
                gmailUnreadFolderMarkedAsReadUniqueIds.Remove(removedMail.UniqueId);
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

                // Create the item. Draft folder navigation is already done at this point.
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

        private IEnumerable<IMailItem> PrepareMailViewModels(IEnumerable<IMailItem> mailItems)
        {
            foreach (var item in mailItems)
            {
                if (item is MailCopy singleMailItem)
                    yield return new MailItemViewModel(singleMailItem);
                else if (item is ThreadMailItem threadMailItem)
                    yield return new ThreadMailItemViewModel(threadMailItem);
            }
        }

        private async Task InitializeFolderAsync()
        {
            if (SelectedFilterOption == null || SelectedFolderPivot == null || SelectedSortingOption == null)
                return;

            try
            {
                MailCollection.Clear();
                MailCollection.MailCopyIdHashSet.Clear();

                SelectedItems.Clear();

                if (ActiveFolder == null)
                    return;

                await ExecuteUIThread(() => { IsInitializingFolder = true; });

                // Folder is changed during initialization.
                // Just cancel the existing one and wait for new initialization.

                if (listManipulationSemepahore.CurrentCount == 0)
                {
                    Debug.WriteLine("Canceling initialization of mails.");

                    listManipulationCancellationTokenSource.Cancel();
                    listManipulationCancellationTokenSource.Token.ThrowIfCancellationRequested();
                }

                listManipulationCancellationTokenSource = new CancellationTokenSource();

                var cancellationToken = listManipulationCancellationTokenSource.Token;

                await listManipulationSemepahore.WaitAsync(cancellationToken);

                // Setup MailCollection configuration.

                // Don't pass any threading strategy if disabled in settings.
                MailCollection.ThreadingStrategyProvider = PreferencesService.IsThreadingEnabled ? _threadingStrategyProvider : null;

                // TODO: This should go inside 
                MailCollection.PruneSingleNonDraftItems = ActiveFolder.SpecialFolderType == SpecialFolderType.Draft;

                // Here items are sorted and filtered.

                var initializationOptions = new MailListInitializationOptions(ActiveFolder.HandlingFolders,
                                                                              SelectedFilterOption.Type,
                                                                              SelectedSortingOption.Type,
                                                                              PreferencesService.IsThreadingEnabled,
                                                                              SelectedFolderPivot.IsFocused,
                                                                              SearchQuery,
                                                                              MailCollection.MailCopyIdHashSet);

                var items = await _mailService.FetchMailsAsync(initializationOptions).ConfigureAwait(false);

                // Here they are already threaded if needed.
                // We don't need to insert them one by one.
                // Just create VMs and do bulk insert.

                var viewModels = PrepareMailViewModels(items);

                await ExecuteUIThread(() => { MailCollection.AddRange(viewModels, true); });
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Initialization of mails canceled.");
            }
            catch (Exception ex)
            {
                Debugger.Break();

                if (IsInSearchMode)
                    Log.Error(ex, WinoErrors.SearchFailed);
                else
                    Log.Error(ex, WinoErrors.MailListRefreshFolder);

                Crashes.TrackError(ex);
            }
            finally
            {
                listManipulationSemepahore.Release();

                await ExecuteUIThread(() =>
                {
                    IsInitializingFolder = false;

                    OnPropertyChanged(nameof(CanSynchronize));
                    NotifyItemFoundState();
                });
            }
        }

        #region Receivers

        void IRecipient<MailItemSelectedEvent>.Receive(MailItemSelectedEvent message)
        {
            if (!SelectedItems.Contains(message.SelectedMailItem)) SelectedItems.Add(message.SelectedMailItem);
        }

        void IRecipient<MailItemSelectionRemovedEvent>.Receive(MailItemSelectionRemovedEvent message)
        {
            if (SelectedItems.Contains(message.RemovedMailItem)) SelectedItems.Remove(message.RemovedMailItem);
        }

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

            // Prepare Focused - Other or folder name tabs.
            UpdateFolderPivots();

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
                case SynchronizationCompletedState.Failed:
                    UpdateBarMessage(InfoBarMessageType.Error, ActiveFolder.FolderName, Translator.SynchronizationFolderReport_Failed);
                    break;
                default:
                    break;
            }
        }

        void IRecipient<MailItemNavigationRequested>.Receive(MailItemNavigationRequested message)
        {
            Debug.WriteLine($"Mail item navigation requested");
            // Find mail item and add to selected items.

            MailItemViewModel navigatingMailItem = null;
            ThreadMailItemViewModel threadMailItemViewModel = null;

            for (int i = 0; i < 3; i++)
            {
                var mailContainer = MailCollection.GetMailItemContainer(message.UniqueMailId);

                if (mailContainer != null)
                {
                    navigatingMailItem = mailContainer.ItemViewModel;
                    threadMailItemViewModel = mailContainer.ThreadViewModel;

                    break;
                }
            }

            if (threadMailItemViewModel != null)
                threadMailItemViewModel.IsThreadExpanded = true;

            if (navigatingMailItem != null)
                WeakReferenceMessenger.Default.Send(new SelectMailItemContainerEvent(navigatingMailItem, message.ScrollToItem));
        }

        #endregion

        public async void Receive(NewSynchronizationRequested message)
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
                    var serverResponse = await _winoServerConnectionManager.GetResponseAsync<bool, SynchronizationExistenceCheckRequest>(new SynchronizationExistenceCheckRequest(accountId));

                    if (serverResponse.IsSuccess && serverResponse.Data == true)
                    {
                        isAnyAccountSynchronizing = true;
                        break;
                    }
                }
            }

            await ExecuteUIThread(() => { IsAccountSynchronizerInSynchronization = isAnyAccountSynchronizing; });
        }

        public void Receive(SelectedMailItemsChanged message) => NotifyItemSelected();
    }
}
