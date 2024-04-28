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
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.Domain.Models.Reader;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Messages.Mails;
using Wino.Core.Messages.Synchronization;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;

namespace Wino.Mail.ViewModels
{
    public partial class MailListPageViewModel : BaseViewModel,
        IRecipient<MailItemNavigationRequested>,
        IRecipient<ActiveMailFolderChangedEvent>,
        IRecipient<MailItemSelectedEvent>,
        IRecipient<MailItemSelectionRemovedEvent>,
        IRecipient<AccountSynchronizationCompleted>,
        IRecipient<NewSynchronizationRequested>,
        IRecipient<AccountSynchronizerStateChanged>
    {
        private bool isChangingFolder = false;

        private Guid? trackingSynchronizationId = null;
        private int completedTrackingSynchronizationCount = 0;

        /* [Bug] Unread folder reads All emails automatically with setting "Mark as Read: When Selected" enabled 
         * https://github.com/bkaankose/Wino-Mail/issues/162
         * We store the UniqueIds of the mails that are marked as read in Gmail Unread folder
         * to prevent them from being removed from the list when they are marked as read.
         */

        private HashSet<Guid> gmailUnreadFolderMarkedAsReadUniqueIds = new HashSet<Guid>();

        private IObservable<System.Reactive.EventPattern<NotifyCollectionChangedEventArgs>> selectionChangedObservable = null;

        public WinoMailCollection MailCollection { get; } = new WinoMailCollection();

        public ObservableCollection<MailItemViewModel> SelectedItems { get; set; } = new ObservableCollection<MailItemViewModel>();
        public ObservableCollection<FolderPivotViewModel> PivotFolders { get; set; } = new ObservableCollection<FolderPivotViewModel>();

        private readonly SemaphoreSlim listManipulationSemepahore = new SemaphoreSlim(1);
        private CancellationTokenSource listManipulationCancellationTokenSource = new CancellationTokenSource();

        public IWinoNavigationService NavigationService { get; }
        public IStatePersistanceService StatePersistanceService { get; }
        public IPreferencesService PreferencesService { get; }

        private readonly IMailService _mailService;
        private readonly INotificationBuilder _notificationBuilder;
        private readonly IFolderService _folderService;
        private readonly IWinoSynchronizerFactory _winoSynchronizerFactory;
        private readonly IThreadingStrategyProvider _threadingStrategyProvider;
        private readonly IContextMenuItemService _contextMenuItemService;
        private readonly IWinoRequestDelegator _winoRequestDelegator;
        private readonly IKeyPressService _keyPressService;

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
            new (Translator.FilteringOption_Flagged, FilterOptionType.Flagged)
        ];

        private FolderPivotViewModel _selectedFolderPivot;

        [ObservableProperty]
        private string searchQuery;

        [ObservableProperty]
        private FilterOption _selectedFilterOption;
        private SortingOption _selectedSortingOption;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        [NotifyPropertyChangedFor(nameof(IsCriteriaFailed))]
        [NotifyPropertyChangedFor(nameof(IsFolderEmpty))]
        private bool isInitializingFolder;

        [ObservableProperty]
        private InfoBarMessageType barSeverity;

        [ObservableProperty]
        private string barMessage;

        [ObservableProperty]
        private string barTitle;

        [ObservableProperty]
        private bool isBarOpen;

        public MailListPageViewModel(IDialogService dialogService,
                                     IWinoNavigationService navigationService,
                                     IMailService mailService,
                                     INotificationBuilder notificationBuilder,
                                     IStatePersistanceService statePersistanceService,
                                     IFolderService folderService,
                                     IWinoSynchronizerFactory winoSynchronizerFactory,
                                     IThreadingStrategyProvider threadingStrategyProvider,
                                     IContextMenuItemService contextMenuItemService,
                                     IWinoRequestDelegator winoRequestDelegator,
                                     IKeyPressService keyPressService,
                                     IPreferencesService preferencesService) : base(dialogService)
        {
            PreferencesService = preferencesService;
            StatePersistanceService = statePersistanceService;
            NavigationService = navigationService;

            _mailService = mailService;
            _notificationBuilder = notificationBuilder;
            _folderService = folderService;
            _winoSynchronizerFactory = winoSynchronizerFactory;
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
        }

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

                Messenger.Send(new NewSynchronizationRequested(options));
            }
        }

        private async void ActiveMailItemChanged(MailItemViewModel selectedMailItemViewModel)
        {
            if (_activeMailItem == selectedMailItemViewModel) return;

            // Don't update active mail item if Ctrl key is pressed.
            // User is probably trying to select multiple items.
            // This is not the same behavior in Windows Mail,
            // but it's a trash behavior.

            var isCtrlKeyPressed = _keyPressService.IsCtrlKeyPressed();

            if (isCtrlKeyPressed) return;

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

        public bool CanSynchronize => !IsAccountSynchronizerInSynchronization && IsFolderSynchronizationEnabled;

        public bool IsFolderSynchronizationEnabled => ActiveFolder?.IsSynchronizationEnabled ?? false;

        #region Properties

        public int SelectedItemCount => SelectedItems.Count;
        public bool HasMultipleItemSelections => SelectedItemCount > 1;
        public bool HasSelectedItems => SelectedItems.Any();
        public bool IsArchiveSpecialFolder => ActiveFolder?.SpecialFolderType == SpecialFolderType.Archive;
        public bool IsEmpty => !IsPerformingSearch && MailCollection.Count == 0;
        public bool IsCriteriaFailed => IsEmpty && IsInSearchMode;
        public bool IsFolderEmpty => !IsInitializingFolder && IsEmpty && !IsInSearchMode;

        private bool _isPerformingSearch;

        public bool IsPerformingSearch
        {
            get => _isPerformingSearch;
            set
            {
                if (SetProperty(ref _isPerformingSearch, value))
                {
                    NotifyItemFoundState();
                }
            }
        }

        public bool IsInSearchMode => !string.IsNullOrEmpty(SearchQuery);

        #endregion

        public void NotifyItemSelected()
        {
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

        [RelayCommand]
        public Task ExecuteHoverAction(MailOperationPreperationRequest request) => ExecuteMailOperationAsync(request);

        public Task ExecuteMailOperationAsync(MailOperationPreperationRequest package) => _winoRequestDelegator.ExecuteAsync(package);

        protected override void OnDispatcherAssigned()
        {
            base.OnDispatcherAssigned();

            MailCollection.CoreDispatcher = Dispatcher;
        }

        protected override async void OnFolderUpdated(MailItemFolder updatedFolder, MailAccount account)
        {
            base.OnFolderUpdated(updatedFolder, account);

            // Don't need to update if the folder update does not belong to the current folder menu item.
            if (ActiveFolder == null || updatedFolder == null || !ActiveFolder.HandlingFolders.Any(a => a.Id == updatedFolder.Id)) return;

            await ExecuteUIThread(() =>
            {
                ActiveFolder.UpdateFolder(updatedFolder);

                OnPropertyChanged(nameof(CanSynchronize));
                OnPropertyChanged(nameof(IsFolderSynchronizationEnabled));
            });

            // Force synchronization after enabling the folder.
            SyncFolder();
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
                    contextMailItems = new List<MailItemViewModel>() { clickedMailItemViewModel };
            }

            return contextMailItems;
        }

        public IEnumerable<MailOperationMenuItem> GetAvailableMailActions(IEnumerable<IMailItem> contextMailItems)
            => _contextMenuItemService.GetMailItemContextMenuActions(contextMailItems);

        public void ChangeCustomFocusedState(IEnumerable<IMailItem> mailItems, bool isFocused)
            => mailItems.Where(a => a is MailItemViewModel).Cast<MailItemViewModel>().ForEach(a => a.IsCustomFocused = isFocused);

        private bool ShouldPreventItemAdd(IMailItem mailItem)
        {
            bool condition2 = false;

            bool condition1 = mailItem.IsRead
                              && SelectedFilterOption.Type == FilterOptionType.Unread
                              || !mailItem.IsFlagged
                              && SelectedFilterOption.Type == FilterOptionType.Flagged;

            return condition1 || condition2;
        }

        protected override async void OnMailAdded(MailCopy addedMail)
        {
            base.OnMailAdded(addedMail);

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

                await ExecuteUIThread(async () =>
                {
                    await MailCollection.AddAsync(addedMail);

                    NotifyItemFoundState();
                });
            }
            catch (Exception) { }
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
                await ExecuteUIThread(async () =>
                {
                    await MailCollection.AddAsync(draftMail);

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
                                                                          SearchQuery,
                                                                          MailCollection.MailCopyIdHashSet);

            var items = await _mailService.FetchMailsAsync(initializationOptions).ConfigureAwait(false);

            var viewModels = PrepareMailViewModels(items);

            await ExecuteUIThread(() => { MailCollection.AddRange(viewModels, clearIdCache: false); });
            await ExecuteUIThread(() => { IsInitializingFolder = false; });
        }

        private async Task InitializeFolderAsync()
        {
            if (SelectedFilterOption == null || SelectedFolderPivot == null || SelectedSortingOption == null)
                return;

            try
            {
                // Clear search query if not performing search.
                if (!IsPerformingSearch)
                    SearchQuery = string.Empty;

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

        [RelayCommand]
        private async Task EnableFolderSynchronizationAsync()
        {
            if (ActiveFolder == null) return;

            foreach (var folder in ActiveFolder.HandlingFolders)
            {
                await _folderService.ChangeFolderSynchronizationStateAsync(folder.Id, true);
            }

            // TODO
            //ActiveFolder.IsSynchronizationEnabled = true;

            //OnPropertyChanged(nameof(IsFolderSynchronizationEnabled));
            //OnPropertyChanged(nameof(CanSynchronize));

            //SyncFolderCommand?.Execute(null);
        }

        void IRecipient<MailItemNavigationRequested>.Receive(MailItemNavigationRequested message)
        {
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
            else
                Debugger.Break();
        }

        async void IRecipient<ActiveMailFolderChangedEvent>.Receive(ActiveMailFolderChangedEvent message)
        {
            isChangingFolder = true;

            ActiveFolder = message.BaseFolderMenuItem;
            gmailUnreadFolderMarkedAsReadUniqueIds.Clear();

            trackingSynchronizationId = null;
            completedTrackingSynchronizationCount = 0;

            // Check whether the account synchronizer that this folder belongs to is already in synchronization.
            await CheckIfAccountIsSynchronizingAsync();

            // Notify change for archive-unarchive app bar button.
            OnPropertyChanged(nameof(IsArchiveSpecialFolder));

            // Prepare Focused - Other or folder name tabs.
            UpdateFolderPivots();

            await InitializeFolderAsync();

            // TODO: This should be done in a better way.
            while (IsInitializingFolder)
            {
                await Task.Delay(100);
            }

            // Let awaiters know about the completion of mail init.
            message.FolderInitLoadAwaitTask?.TrySetResult(true);

            isChangingFolder = false;
        }

        void IRecipient<MailItemSelectedEvent>.Receive(MailItemSelectedEvent message)
            => SelectedItems.Add(message.SelectedMailItem);

        void IRecipient<MailItemSelectionRemovedEvent>.Receive(MailItemSelectionRemovedEvent message)
            => SelectedItems.Remove(message.RemovedMailItem);

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

        public async void Receive(NewSynchronizationRequested message)
            => await ExecuteUIThread(() => { OnPropertyChanged(nameof(CanSynchronize)); });

        [RelayCommand]
        public async Task PerformSearchAsync()
        {
            try
            {
                IsPerformingSearch = !string.IsNullOrEmpty(SearchQuery);

                await InitializeFolderAsync();
            }
            finally
            {
                IsPerformingSearch = false;
            }
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
                    var synchronizer = _winoSynchronizerFactory.GetAccountSynchronizer(accountId);

                    if (synchronizer == null) continue;

                    bool isAccountSynchronizing = synchronizer.State != AccountSynchronizerState.Idle;

                    if (isAccountSynchronizing)
                    {
                        isAnyAccountSynchronizing = true;
                        break;
                    }
                }
            }

            await ExecuteUIThread(() => { IsAccountSynchronizerInSynchronization = isAnyAccountSynchronizing; });
        }
    }
}
