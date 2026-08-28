using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Domain.Misc;
using Wino.Core.Requests;
using Wino.Core.Requests.Tasks;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// To Do mode state and shell menu. Provider data is kept in ITaskService; this VM only
/// projects the selected list/view and performs optimistic local mutations.
/// </summary>
public partial class ToDoPageViewModel : MailBaseViewModel, IShellMenuOwner, IShellMenuProvider,
    IRecipient<TaskSynchronizationCompleted>,
    IRecipient<TaskStateChanged>,
    IRecipient<AccountUpdatedMessage>,
    IRecipient<AccountRemovedMessage>
{
    private readonly ITaskQueryService _taskService;
    private readonly ITaskService _taskMutationService;
    private readonly IAccountService _accountService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly INavigationService _navigationService;
    private readonly ICalendarService _calendarService;
    private readonly IMailDialogService _dialogService;
    private readonly NewTaskListMenuItem _newListMenuItem = new();
    private readonly TaskSyncMenuItem _taskSyncMenuItem = new();
    private readonly SeperatorItem _commandSeparator = new();
    private readonly SeperatorItem _smartViewSeparator = new();
    private readonly SeperatorItem _accountSeparator = new();
    private readonly MyDayTaskMenuItem _myDayMenuItem = new();
    private readonly PlannedTaskMenuItem _plannedMenuItem = new();
    private readonly ImportantTaskMenuItem _importantMenuItem = new();
    private readonly Dictionary<Guid, AccountTaskListAccountMenuItem> _accountGroupMenuItems = [];
    private readonly Dictionary<Guid, AccountTaskListGroupMenuItem> _taskListGroupMenuItems = [];
    private readonly Dictionary<Guid, AccountTaskListMenuItem> _listMenuItems = [];
    private readonly ObservableCollection<TaskGroup> _taskGroups = [];
    private readonly Dictionary<Guid, TaskItemViewModel> _taskItems = [];
    private readonly List<Guid> _taskOrder = [];
    private readonly Dictionary<Guid, AccountTask> _menuCountTasks = [];
    private readonly Dictionary<Guid, AccountTask> _pendingTaskStates = [];
    private readonly Dictionary<Guid, Guid> _pendingDeletedTaskIds = [];
    private readonly Dictionary<Guid, AccountTaskList> _pendingListStates = [];
    private readonly Dictionary<Guid, Guid> _pendingDeletedListIds = [];
    private readonly SemaphoreSlim _reloadSemaphore = new(1, 1);
    private bool _isPaneCompact;
    private bool _isPreparedForShellShutdown;
    private int _suppressSurfaceReloadDepth;
    private object _selectedMenuItem;
    private long _requestedFullReloadVersion;
    private long _completedFullReloadVersion;
    private long _taskReloadVersion;
    private long _accountLoadVersion;
    private Guid? _selectedAccountId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    [NotifyCanExecuteChangedFor(nameof(ClearFiltersCommand))]
    public partial string FilterText { get; set; } = string.Empty;

    partial void OnFilterTextChanged(string value)
    {
        if (_suppressSurfaceReloadDepth == 0)
            ReconcileTaskGroups();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuickAddVisible))]
    [NotifyPropertyChangedFor(nameof(IsMyDaySelected))]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    public partial TaskViewKind SelectedView { get; set; } = TaskViewKind.MyDay;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTaskCommand))]
    public partial AccountTaskList SelectedList { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailVisible))]
    [NotifyPropertyChangedFor(nameof(CanEditSelectedTask))]
    public partial TaskItemViewModel SelectedTask { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? SelectedTaskDueDate { get; set; }

    [ObservableProperty]
    public partial bool CanCreateCalendarEvent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskListSurfaceVisible))]
    [NotifyPropertyChangedFor(nameof(IsDetailSurfaceVisible))]
    public partial bool IsCompactLayout { get; set; }

    /// <summary>Client-side ordering. Persisted nowhere; resets with the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDisplayText))]
    public partial TaskSortKind SelectedSort { get; set; } = TaskSortKind.DueDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestions))]
    public partial bool IsSuggestionsOpen { get; set; }

    [ObservableProperty]
    public partial string ComposerText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsComposerExpanded { get; set; }

    /// <summary>Due date staged in the composer, applied to the task on add.</summary>
    [ObservableProperty]
    public partial DateTime? ComposerDueDate { get; set; }

    /// <summary>
    /// Which completion states the surface lists. A single choice, so there is no invalid
    /// combination for the surface to correct after the fact.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompletionScopeDisplayText))]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    [NotifyCanExecuteChangedFor(nameof(ClearFiltersCommand))]
    public partial TaskCompletionScope SelectedCompletionScope { get; set; } = TaskCompletionScope.Active;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    [NotifyCanExecuteChangedFor(nameof(ClearFiltersCommand))]
    public partial bool IsImportantTasksFilterSelected { get; set; }

    partial void OnSelectedSortChanged(TaskSortKind value)
        => _ = ReloadTasksAsync();

    partial void OnSelectedCompletionScopeChanged(TaskCompletionScope value) => ApplyFilterChange();

    partial void OnIsImportantTasksFilterSelectedChanged(bool value) => ApplyFilterChange();

    private void ApplyFilterChange()
    {
        if (_suppressSurfaceReloadDepth == 0)
            ReconcileTaskGroups();
    }

    partial void OnSelectedTaskChanged(TaskItemViewModel value)
    {
        SelectedTaskDueDate = value?.DueDate is { } dueDate
            ? new DateTimeOffset(dueDate)
            : null;
        CanCreateCalendarEvent = false;
        OnPropertyChanged(nameof(IsTaskListSurfaceVisible));
        OnPropertyChanged(nameof(IsDetailSurfaceVisible));
        OnPropertyChanged(nameof(CanEditSelectedTask));
        _ = RefreshCalendarEventAvailabilityAsync(value);
    }

    partial void OnSelectedTaskDueDateChanged(DateTimeOffset? value)
    {
        if (CanEditSelectedTask && SelectedTask != null)
            SelectedTask.DueDate = value?.Date;
    }

    partial void OnSelectedListChanged(AccountTaskList value)
    {
        if (value is not null)
        {
            SelectedView = TaskViewKind.All;

            if (_selectedAccountId != value.MailAccountId)
                _ = ChangeSelectedAccountAsync(value.MailAccountId, selectDefaultList: false);
        }

        OnPropertyChanged(nameof(CanCreateTask));
        OnPropertyChanged(nameof(IsQuickAddVisible));
        OnPropertyChanged(nameof(IsNamedListSelected));
        OnPropertyChanged(nameof(IsSmartViewSelected));
        OnPropertyChanged(nameof(IsCompletedScopeAvailable));
        OnPropertyChanged(nameof(IsMyDaySelected));
        OnPropertyChanged(nameof(CanEditSelectedList));
        OnPropertyChanged(nameof(CanDeleteSelectedList));
        OnPropertyChanged(nameof(IsSelectedListReadOnly));
        OnPropertyChanged(nameof(CanEditSelectedTask));
        OnPropertyChanged(nameof(SelectedSurfaceTitle));
        OnPropertyChanged(nameof(SelectedSurfaceSubtitle));
        OnPropertyChanged(nameof(HasSurfaceSubtitle));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        RenameSelectedListCommand.NotifyCanExecuteChanged();
        UpdateSelectedMenuItemReference();
        if (_suppressSurfaceReloadDepth == 0)
            _ = ReloadTasksAsync();
    }

    partial void OnSelectedViewChanged(TaskViewKind value)
    {
        IsSuggestionsOpen = false;
        OnPropertyChanged(nameof(SelectedSurfaceTitle));
        OnPropertyChanged(nameof(SelectedSurfaceSubtitle));
        OnPropertyChanged(nameof(HasSurfaceSubtitle));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(CanCreateTask));
        OnPropertyChanged(nameof(IsQuickAddVisible));
        if (SelectedList is null && _suppressSurfaceReloadDepth == 0)
            _ = ReloadTasksAsync();
    }

    public ObservableCollection<AccountTaskList> TaskLists { get; } = [];
    public ObservableCollection<AccountTaskListGroup> TaskListGroups { get; } = [];
    public ReadOnlyObservableCollection<TaskGroup> TaskGroups { get; }
    public ObservableCollection<TaskItemViewModel> Suggestions { get; } = [];
    public ObservableCollection<MailAccount> Accounts { get; } = [];

    public bool IsEmpty => !IsLoading && TaskGroups.Sum(group => group.Count) == 0;
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool IsDetailVisible => SelectedTask is not null;
    public bool IsTaskListSurfaceVisible => !IsCompactLayout || SelectedTask is null;
    /// <summary>The drawer collapses to zero width when nothing is selected, in either layout.</summary>
    public bool IsDetailSurfaceVisible => SelectedTask is not null;
    public bool CanCreateTask => SelectedList is { IsReadOnly: false } ||
                                 (SelectedList is null && SelectedView != TaskViewKind.Completed && GetWritableDestinationList() is not null);
    public bool IsQuickAddVisible => SelectedList is not null || SelectedView != TaskViewKind.Completed;
    public bool IsNamedListSelected => SelectedList is not null;
    public bool IsSmartViewSelected => SelectedList is null;
    public bool IsMyDaySelected => SelectedList is null && SelectedView == TaskViewKind.MyDay;
    public bool CanEditSelectedList => SelectedList is { IsReadOnly: false };
    public bool CanDeleteSelectedList => SelectedList is { IsReadOnly: false, IsOutlookDefaultList: false };
    public bool CanEditSelectedTask => SelectedTask is { IsReadOnly: false };
    public bool IsSelectedListReadOnly => SelectedList?.IsReadOnly ?? true;

    public string SelectedSurfaceTitle => SelectedList?.Title ?? SelectedView switch
    {
        TaskViewKind.MyDay => Translator.ToDoPage_MyDay,
        TaskViewKind.Important => Translator.ToDoPage_Important,
        TaskViewKind.Planned => Translator.ToDoPage_Planned,
        TaskViewKind.Completed => Translator.ToDoPage_Completed,
        _ => Translator.ToDoPage_Tasks
    };

    /// <summary>
    /// Carries today's date on My Day, and always the count. The count lives here rather than
    /// under the command bar so filtering never costs the header a second row.
    /// </summary>
    public string SelectedSurfaceSubtitle
        => IsMyDaySelected
            ? $"{DateTime.Now:dddd, MMMM d} • {TaskCountText}"
            : TaskCountText;

    public bool HasSurfaceSubtitle => !string.IsNullOrEmpty(SelectedSurfaceSubtitle);

    /// <summary>
    /// While filtered this reports what was kept, which is the question a narrowed list raises.
    /// Otherwise it reports what the surface holds.
    /// </summary>
    private string TaskCountText
    {
        get
        {
            if (IsFiltered)
                return string.Format(Translator.ToDoPage_ShowingCount, VisibleTaskCount, TotalTaskCount);

            var completed = _taskItems.Values.Count(item => item.IsCompleted);
            var open = _taskItems.Count - completed;
            var openText = string.Format(Translator.ToDoPage_TaskCount, open);

            return completed == 0
                ? openText
                : $"{openText} • {string.Format(Translator.ToDoPage_CompletedCount, completed)}";
        }
    }

    private int VisibleTaskCount => _taskGroups.Sum(group => group.Count);
    private int TotalTaskCount => _taskItems.Count;

    /// <summary>True when anything narrows the surface. Drives the Clear command and the count text.</summary>
    public bool IsFiltered
        => !string.IsNullOrWhiteSpace(FilterText) ||
           SelectedCompletionScope != TaskCompletionScope.Active ||
           IsImportantTasksFilterSelected;

    public string CompletionScopeDisplayText => SelectedCompletionScope switch
    {
        TaskCompletionScope.Completed => Translator.ToDoPage_Completed,
        TaskCompletionScope.All => Translator.ToDoPage_All,
        _ => Translator.ToDoPage_ScopeActive
    };

    /// <summary>Smart views never load completed tasks, so that scope has nothing to show there.</summary>
    public bool IsCompletedScopeAvailable => IsNamedListSelected;

    public string ComposerPlaceholder
        => IsMyDaySelected ? Translator.ToDoPage_AddTaskToMyDayPlaceholder : Translator.ToDoPage_AddTaskPlaceholder;

    public string SortDisplayText => SelectedSort switch
    {
        TaskSortKind.Importance => Translator.ToDoPage_SortImportance,
        TaskSortKind.MyDay => Translator.ToDoPage_SortMyDay,
        TaskSortKind.Alphabetical => Translator.ToDoPage_SortAlphabetically,
        TaskSortKind.CreationDate => Translator.ToDoPage_SortCreationDate,
        _ => Translator.ToDoPage_SortDueDate
    };

    public string EmptyStateTitle => SelectedList is not null
        ? Translator.ToDoPage_ListEmptyTitle
        : SelectedView switch
        {
            TaskViewKind.MyDay => Translator.ToDoPage_MyDayEmptyTitle,
            TaskViewKind.Important => Translator.ToDoPage_ImportantEmptyTitle,
            _ => Translator.ToDoPage_ListEmptyTitle
        };

    public string EmptyStateBody => SelectedList is not null
        ? Translator.ToDoPage_ListEmptyBody
        : SelectedView switch
        {
            TaskViewKind.MyDay => Translator.ToDoPage_MyDayEmptyBody,
            TaskViewKind.Important => Translator.ToDoPage_ImportantEmptyBody,
            _ => Translator.ToDoPage_ListEmptyBody
        };

    public IShellMenuProvider ShellMenuProvider => this;
    public WinoApplicationMode Mode => WinoApplicationMode.Tasks;
    public ShellMenu ShellMenu { get; private set; }

    object IShellMenuProvider.SelectedMenuItem
    {
        get => _selectedMenuItem;
        set
        {
            if (ReferenceEquals(_selectedMenuItem, value))
                return;

            _selectedMenuItem = value;
            OnPropertyChanged(nameof(IShellMenuProvider.SelectedMenuItem));
            switch (value)
            {
                case TaskSmartViewMenuItem smartView:
                    SelectSurface(smartView.Parameter, null);
                    break;
                case AccountTaskListMenuItem list:
                    SelectSurface(TaskViewKind.All, list.Parameter);
                    break;
            }
        }
    }

    public ToDoPageViewModel(
        ITaskQueryService taskService,
        IAccountService accountService,
        IWinoRequestDelegator requestDelegator,
        INavigationService navigationService,
        ICalendarService calendarService,
        IMailDialogService dialogService)
    {
        _taskService = taskService;
        _taskMutationService = taskService as ITaskService;
        _accountService = accountService;
        _requestDelegator = requestDelegator;
        _navigationService = navigationService;
        _calendarService = calendarService;
        _dialogService = dialogService;
        TaskGroups = new ReadOnlyObservableCollection<TaskGroup>(_taskGroups);
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();
        _isPreparedForShellShutdown = false;
        ShellMenu = new ShellMenu
        {
            Items = new MenuItemCollection(Dispatcher),
            FooterItems = new MenuItemCollection(Dispatcher),
            HandlesSelection = true
        };
        _newListMenuItem.NewGroupRequested = CreateGroupAsync;

        foreach (var item in GetShellMenuPrefix())
            ShellMenu.Items.Add(item);

        SyncShellMenuItems();
        OnPropertyChanged(nameof(IShellMenuProvider.ShellMenu));
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        Messenger.Register<TaskSynchronizationCompleted>(this);
        Messenger.Register<TaskStateChanged>(this);
        Messenger.Register<AccountUpdatedMessage>(this);
        Messenger.Register<AccountRemovedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        Messenger.Unregister<TaskSynchronizationCompleted>(this);
        Messenger.Unregister<TaskStateChanged>(this);
        Messenger.Unregister<AccountUpdatedMessage>(this);
        Messenger.Unregister<AccountRemovedMessage>(this);
        base.UnregisterRecipients();
    }

    public void Receive(TaskSynchronizationCompleted message)
        => _ = HandleTaskSynchronizationCompletedAsync(message);

    void IRecipient<TaskStateChanged>.Receive(TaskStateChanged message)
        => _ = ExecuteUIThread(() => ApplyTaskState(message));

    void IRecipient<AccountUpdatedMessage>.Receive(AccountUpdatedMessage message)
        => _ = ExecuteUIThread(() => ApplyAccountUpdate(message.Account));

    void IRecipient<AccountRemovedMessage>.Receive(AccountRemovedMessage message)
        => _ = ExecuteUIThread(() => ApplyAccountRemoval(message.Account.Id));

    private void ApplyTaskState(TaskStateChanged message)
    {
        if (message is null)
            return;

        TrackPendingState(message);

        if (message.List is not null)
            ApplyTaskListState(message);

        if (message.Group is not null)
            ApplyTaskListGroupState(message);

        if (message.Task is not null)
            ApplyTaskItemState(message);

        if (message.Step is not null)
            ApplyTaskStepState(message);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanCreateTask));
        ApplyMenuCounts();
    }

    private void TrackPendingState(TaskStateChanged message)
    {
        if (message.Source == EntityUpdateSource.ClientReverted)
        {
            if (message.Task is not null)
            {
                _pendingTaskStates.Remove(message.Task.Id);
                _pendingDeletedTaskIds.Remove(message.Task.Id);
            }

            if (message.List is not null)
            {
                _pendingListStates.Remove(message.List.Id);
                _pendingDeletedListIds.Remove(message.List.Id);
            }

            return;
        }

        if (message.Source != EntityUpdateSource.ClientUpdated)
            return;

        if (message.Task is not null)
        {
            if (message.Change == OptimisticEntityChange.Delete)
            {
                _pendingTaskStates.Remove(message.Task.Id);
                _pendingDeletedTaskIds[message.Task.Id] = message.Task.MailAccountId;
            }
            else
            {
                _pendingDeletedTaskIds.Remove(message.Task.Id);
                _pendingTaskStates[message.Task.Id] = RequestEntityCloner.Task(message.Task);
            }
        }

        if (message.List is not null)
        {
            if (message.Change == OptimisticEntityChange.Delete)
            {
                _pendingListStates.Remove(message.List.Id);
                _pendingDeletedListIds[message.List.Id] = message.List.MailAccountId;
            }
            else
            {
                _pendingDeletedListIds.Remove(message.List.Id);
                _pendingListStates[message.List.Id] = RequestEntityCloner.TaskList(message.List);
            }
        }
    }

    private void ApplyTaskListGroupState(TaskStateChanged message)
    {
        var existing = TaskListGroups.FirstOrDefault(group => group.Id == message.Group.Id);
        if (message.Change == OptimisticEntityChange.Delete)
        {
            if (existing is not null)
                TaskListGroups.Remove(existing);
            _taskListGroupMenuItems.Remove(message.Group.Id);
            SyncShellMenuItems();
            return;
        }

        if (existing is null)
            TaskListGroups.Add(message.Group);
        else
        {
            var index = TaskListGroups.IndexOf(existing);
            TaskListGroups[index] = message.Group;
        }
        ReconcileTaskListGroups(TaskListGroups.ToList());
        SyncShellMenuItems();
    }

    private void ApplyTaskListState(TaskStateChanged message)
    {
        var existing = TaskLists.FirstOrDefault(item => item.Id == message.List.Id);

        if (message.Change == OptimisticEntityChange.Delete)
        {
            if (existing is not null)
                TaskLists.Remove(existing);

            _listMenuItems.Remove(message.List.Id);
            if (SelectedList?.Id == message.List.Id)
                SelectSurface(TaskViewKind.MyDay, null);

            SyncShellMenuItems();
            return;
        }

        var desiredLists = TaskLists.Where(item => item.Id != message.List.Id).Append(message.List).ToList();
        ReconcileTaskLists(desiredLists);

        if (SelectedList?.Id == message.List.Id)
        {
            _suppressSurfaceReloadDepth++;
            try
            {
                SelectedList = message.List;
            }
            finally
            {
                _suppressSurfaceReloadDepth--;
            }

            foreach (var item in _taskItems.Values.Where(item => item.Task.TaskListId == message.List.Id))
                item.ListName = message.List.Title ?? string.Empty;
        }

        if (!TryApplyListMetadataOnly(message.List))
            SyncShellMenuItems();
    }

    /// <summary>
    /// A rename or a recolour leaves the list exactly where it was in the pane. Refreshing the
    /// existing menu item is enough, and it spares the NavigationView a reconcile pass that would
    /// otherwise rebuild containers and disturb the selection.
    /// </summary>
    private bool TryApplyListMetadataOnly(AccountTaskList list)
    {
        if (!_listMenuItems.TryGetValue(list.Id, out var item))
            return false;

        if (item.Parameter.GroupId != list.GroupId || item.Parameter.SortOrder != list.SortOrder)
            return false;

        var account = Accounts.FirstOrDefault(candidate => candidate.Id == list.MailAccountId);
        if (account is null)
            return false;

        var accountGroups = TaskListGroups.Where(group => group.MailAccountId == account.Id)
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Id)
            .ToList();

        item.Update(list, account.Name, accountGroups);
        return true;
    }

    private void ApplyTaskItemState(TaskStateChanged message)
    {
        if (message.Change == OptimisticEntityChange.Delete)
        {
            _menuCountTasks.Remove(message.Task.Id);
            _taskItems.Remove(message.Task.Id);
            _taskOrder.Remove(message.Task.Id);

            var suggestion = Suggestions.FirstOrDefault(item => item.Id == message.Task.Id);
            if (suggestion is not null)
                Suggestions.Remove(suggestion);
            if (SelectedTask?.Id == message.Task.Id)
                SelectedTask = null;

            ReconcileTaskGroups();
            return;
        }

        _menuCountTasks[message.Task.Id] = message.Task;

        var suggestionItem = Suggestions.FirstOrDefault(item => item.Id == message.Task.Id);
        if (suggestionItem is not null)
        {
            if (message.Task.IsCompleted || message.Task.MyDayDateUtc == DateTime.UtcNow.Date)
                Suggestions.Remove(suggestionItem);
            else
                suggestionItem.ApplySnapshot(message.Task);
        }

        if (!TaskMatchesCurrentSurface(message.Task))
        {
            _taskItems.Remove(message.Task.Id);
            _taskOrder.Remove(message.Task.Id);
            if (SelectedTask?.Id == message.Task.Id)
                SelectedTask = null;

            ReconcileTaskGroups();
            return;
        }

        if (!_taskItems.TryGetValue(message.Task.Id, out var existing))
        {
            existing = new TaskItemViewModel(message.Task, ResolveListName(message.Task));
            _taskItems.Add(message.Task.Id, existing);
            _taskOrder.Add(message.Task.Id);
        }
        else
        {
            existing.ApplySnapshot(message.Task);
            existing.ListName = ResolveListName(message.Task);
        }

        existing.ShowListName = SelectedList is null;
        if (SelectedTask?.Id == message.Task.Id)
            SelectedTask = existing;

        ReconcileTaskGroups();
    }

    private void ApplyTaskStepState(TaskStateChanged message)
    {
        var owner = _taskItems.GetValueOrDefault(message.Step.TaskId)
            ?? Suggestions.FirstOrDefault(item => item.Id == message.Step.TaskId);

        if (owner is null)
            return;

        var existing = owner.Steps.FirstOrDefault(item => item.Step.Id == message.Step.Id);
        if (message.Change == OptimisticEntityChange.Delete)
        {
            if (existing is not null)
                owner.Steps.Remove(existing);
            owner.RefreshStepSummary();
            return;
        }

        if (existing is null)
            owner.Steps.Add(new TaskStepViewModel(message.Step));
        else
            existing.ApplySnapshot(message.Step);

        owner.RefreshStepSummary();
    }

    private void ApplyAccountUpdate(MailAccount account)
    {
        if (account is null)
            return;

        if (!account.IsTaskAccessEnabled)
        {
            ApplyAccountRemoval(account.Id);
            return;
        }

        ReconcileAccounts(Accounts.Where(item => item.Id != account.Id).Append(account).ToList());

        if (_accountGroupMenuItems.TryGetValue(account.Id, out var header))
            header.UpdateAccount(account);

        SyncShellMenuItems();
        ReconcileTaskGroups();
    }

    private void ApplyAccountRemoval(Guid accountId)
    {
        var account = Accounts.FirstOrDefault(item => item.Id == accountId);
        if (account is not null)
            Accounts.Remove(account);

        foreach (var list in TaskLists.Where(item => item.MailAccountId == accountId).ToList())
        {
            TaskLists.Remove(list);
            _listMenuItems.Remove(list.Id);
        }

        foreach (var group in TaskListGroups.Where(item => item.MailAccountId == accountId).ToList())
        {
            TaskListGroups.Remove(group);
            _taskListGroupMenuItems.Remove(group.Id);
        }

        _accountGroupMenuItems.Remove(accountId);
        foreach (var taskId in _menuCountTasks.Values.Where(task => task.MailAccountId == accountId).Select(task => task.Id).ToList())
            _menuCountTasks.Remove(taskId);

        foreach (var taskId in _taskItems.Values.Where(item => item.Task.MailAccountId == accountId).Select(item => item.Id).ToList())
        {
            _taskItems.Remove(taskId);
            _taskOrder.Remove(taskId);
        }

        if (SelectedList?.MailAccountId == accountId)
            SelectSurface(TaskViewKind.MyDay, null);

        if (_selectedAccountId == accountId)
            _selectedAccountId = Accounts.FirstOrDefault()?.Id;

        SyncShellMenuItems();
        ReconcileTaskGroups();
        ApplyMenuCounts();
    }

    private async Task HandleTaskSynchronizationCompletedAsync(TaskSynchronizationCompleted message)
    {
        var isKnownAccount = false;
        await ExecuteUIThread(() =>
        {
            isKnownAccount = Accounts.Any(account => account.Id == message.AccountId);
            ClearPendingState(message.AccountId);
        }).ConfigureAwait(false);
        if (isKnownAccount)
            await ReloadAsync().ConfigureAwait(false);
    }

    private void ClearPendingState(Guid accountId)
    {
        foreach (var taskId in _pendingTaskStates.Values
                     .Where(task => task.MailAccountId == accountId)
                     .Select(task => task.Id)
                     .Concat(_pendingDeletedTaskIds.Where(pair => pair.Value == accountId).Select(pair => pair.Key))
                     .Distinct()
                     .ToList())
        {
            _pendingTaskStates.Remove(taskId);
            _pendingDeletedTaskIds.Remove(taskId);
        }

        foreach (var listId in _pendingListStates.Values
                     .Where(list => list.MailAccountId == accountId)
                     .Select(list => list.Id)
                     .Concat(_pendingDeletedListIds.Where(pair => pair.Value == accountId).Select(pair => pair.Key))
                     .Distinct()
                     .ToList())
        {
            _pendingListStates.Remove(listId);
            _pendingDeletedListIds.Remove(listId);
        }
    }

    public override void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _isPreparedForShellShutdown = false;
        _ = ReloadAsync();
    }

    public override void OnNavigatedFrom(NavigationMode mode, object parameters)
    {
        base.OnNavigatedFrom(mode, parameters);
        SelectedTask = null;
    }

    public void ActivateShellMenu(ShellModeActivationContext activationContext)
        => _navigationService.Navigate(WinoPage.ToDoPage, activationContext?.Parameter);

    public void SetPaneCompact(bool isCompact)
    {
        if (_isPaneCompact == isCompact)
            return;
        _isPaneCompact = isCompact;
        _ = ExecuteUIThread(SyncShellMenuItems);
    }

    public void SetCompactLayout(bool isCompact)
    {
        if (IsCompactLayout == isCompact)
            return;

        IsCompactLayout = isCompact;
        if (isCompact && SelectedTask is null)
            OnPropertyChanged(nameof(IsTaskListSurfaceVisible));
    }

    public async Task<IReadOnlyList<AccountTask>> SearchTasksAsync(string queryText, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queryText) || limit <= 0)
            return [];

        var tasks = await _taskService.GetTasksAsync(search: queryText.Trim()).ConfigureAwait(false) ?? [];
        cancellationToken.ThrowIfCancellationRequested();
        return tasks.Take(limit).ToList();
    }

    public string GetTaskSearchSubtitle(AccountTask task)
    {
        if (task is null)
            return string.Empty;

        return string.Join(" • ", new[] { GetAccountName(task.MailAccountId), ResolveListName(task) }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    public async Task<TaskItemViewModel> LoadAndSelectTaskAsync(Guid taskId)
    {
        var task = await _taskService.GetTaskAsync(taskId).ConfigureAwait(false);
        if (task is null)
            return null;

        var list = await _taskService.GetTaskListAsync(task.TaskListId).ConfigureAwait(false);
        if (list is null)
            return null;

        await ExecuteUIThread(() =>
        {
            _suppressSurfaceReloadDepth++;
            try
            {
                FilterText = string.Empty;
                SelectedList = TaskLists.FirstOrDefault(candidate => candidate.Id == list.Id) ?? list;
                SelectedView = TaskViewKind.All;

                // Land on the one scope that can actually show this task.
                SelectedCompletionScope = task.IsCompleted
                    ? TaskCompletionScope.Completed
                    : TaskCompletionScope.Active;
                IsImportantTasksFilterSelected = false;
            }
            finally
            {
                _suppressSurfaceReloadDepth--;
            }

            UpdateSelectedMenuItemReference();
        }).ConfigureAwait(false);

        await ReloadTasksAsync().ConfigureAwait(false);

        TaskItemViewModel selectedItem = null;
        await ExecuteUIThread(() =>
        {
            if (_taskItems.TryGetValue(task.Id, out var loadedItem))
            {
                SelectedTask = loadedItem;
                selectedItem = loadedItem;
            }
        }).ConfigureAwait(false);

        return selectedItem;
    }

    public Task OnMenuSelectionChangedAsync(IMenuItem menuItem)
    {
        if (menuItem is TaskSmartViewMenuItem smartView)
            SelectSurface(smartView.Parameter, null);
        else if (menuItem is AccountTaskListMenuItem list)
            SelectSurface(TaskViewKind.All, list.Parameter);

        return Task.CompletedTask;
    }

    public Task OnMenuItemInvokedAsync(IMenuItem menuItem)
    {
        switch (menuItem)
        {
            case NewTaskListMenuItem:
                return CreateListAsync();
            case TaskSyncMenuItem:
                Synchronize();
                return Task.CompletedTask;
            case AccountTaskListAccountMenuItem account:
                return ChangeSelectedAccountAsync(account.Parameter.Id);
            case TaskSmartViewMenuItem smartView:
                SelectSurface(smartView.Parameter, null);
                return Task.CompletedTask;
            case AccountTaskListMenuItem list:
                SelectSurface(TaskViewKind.All, list.Parameter);
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    private async Task ChangeSelectedAccountAsync(Guid accountId, bool selectDefaultList = true)
    {
        if (_selectedAccountId == accountId || Accounts.All(account => account.Id != accountId))
            return;

        var loadVersion = Interlocked.Increment(ref _accountLoadVersion);
        var listsTask = _taskService.GetTaskListsAsync(accountId);
        var groupsTask = _taskService.GetTaskListGroupsAsync(accountId);
        await Task.WhenAll(listsTask, groupsTask).ConfigureAwait(false);
        var lists = listsTask.Result ?? [];
        var groups = groupsTask.Result ?? [];

        if (loadVersion != Volatile.Read(ref _accountLoadVersion))
            return;

        await ExecuteUIThread(() =>
        {
            if (loadVersion != Volatile.Read(ref _accountLoadVersion) ||
                Accounts.All(account => account.Id != accountId))
            {
                return;
            }

            var effectiveLists = lists
                .Where(list => !_pendingDeletedListIds.ContainsKey(list.Id) &&
                               !_pendingListStates.ContainsKey(list.Id))
                .Concat(_pendingListStates.Values.Where(list => list.MailAccountId == accountId))
                .ToList();

            ReconcileTaskLists(TaskLists
                .Where(list => list.MailAccountId != accountId)
                .Concat(effectiveLists)
                .ToList());
            ReconcileTaskListGroups(TaskListGroups
                .Where(group => group.MailAccountId != accountId)
                .Concat(groups)
                .ToList());

            _selectedAccountId = accountId;
            var defaultList = TaskLists
                .Where(list => list.MailAccountId == accountId)
                .OrderByDescending(list => list.IsDefault)
                .ThenBy(list => list.GroupId.HasValue)
                .ThenBy(list => list.SortOrder)
                .ThenBy(list => list.Id)
                .FirstOrDefault();

            SyncShellMenuItems();
            if (selectDefaultList)
                SelectSurface(defaultList is null ? TaskViewKind.MyDay : TaskViewKind.All, defaultList);
            else
                UpdateSelectedMenuItemReference();
        }).ConfigureAwait(false);
    }

    public void ReleaseShellMenu() { }

    public void PrepareForShellShutdown()
    {
        if (_isPreparedForShellShutdown)
            return;
        _isPreparedForShellShutdown = true;
        ShellMenu?.Items.Clear();
        ShellMenu?.FooterItems?.Clear();
        ShellMenu = null;
        TaskLists.Clear();
        _taskGroups.Clear();
        _taskItems.Clear();
        _taskOrder.Clear();
        _menuCountTasks.Clear();
        _pendingTaskStates.Clear();
        _pendingDeletedTaskIds.Clear();
        _pendingListStates.Clear();
        _pendingDeletedListIds.Clear();
        _accountGroupMenuItems.Clear();
        foreach (var groupMenuItem in _taskListGroupMenuItems.Values)
            groupMenuItem.PropertyChanged -= TaskListGroupMenuItem_PropertyChanged;
        _taskListGroupMenuItems.Clear();
        _listMenuItems.Clear();
        Suggestions.Clear();
        Accounts.Clear();
        TaskListGroups.Clear();
        SelectedTask = null;
        SelectedList = null;
        _selectedMenuItem = null;
        _selectedAccountId = null;
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        Interlocked.Increment(ref _requestedFullReloadVersion);
        await _reloadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            while (Volatile.Read(ref _completedFullReloadVersion) < Volatile.Read(ref _requestedFullReloadVersion))
            {
                var requestedVersion = Volatile.Read(ref _requestedFullReloadVersion);
                await ReloadCoreAsync().ConfigureAwait(false);
                Volatile.Write(ref _completedFullReloadVersion, requestedVersion);
            }
        }
        finally
        {
            _reloadSemaphore.Release();
        }
    }

    private async Task ReloadCoreAsync()
    {
        await ExecuteUIThread(() => IsLoading = true).ConfigureAwait(false);
        try
        {
            var accounts = (await _accountService.GetAccountsAsync().ConfigureAwait(false))
                .Where(account => account.IsTaskAccessEnabled)
                .ToList();
            var lists = await _taskService.GetTaskListsAsync().ConfigureAwait(false);
            var listGroups = await _taskService.GetTaskListGroupsAsync().ConfigureAwait(false) ?? [];

            foreach (var account in accounts.Where(RequiresLocalFallbackList))
            {
                if (lists.Any(list => list.MailAccountId == account.Id && list.SourceKind == TaskSourceKind.Local))
                    continue;

                var now = DateTime.UtcNow;
                var localList = new AccountTaskList
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = account.Id,
                    SourceKind = TaskSourceKind.Local,
                    Title = string.IsNullOrWhiteSpace(account.Name) ? Translator.ToDoPage_Tasks : account.Name,
                    ColorHex = ColorPalette.GetDistinctColor(lists.Select(existing => existing.ColorHex)),
                    IsDefault = true,
                    CreatedAtUtc = now,
                    ModifiedAtUtc = now
                };

                lists.Add(localList);
                await QueueMutationAsync(new TaskActionRequest(
                    account.Id,
                    TaskSynchronizerOperation.CreateList,
                    List: localList)).ConfigureAwait(false);
            }

            var allTasks = await _taskService.GetTasksAsync().ConfigureAwait(false) ?? [];

            await ExecuteUIThread(() =>
            {
                var selectedListId = SelectedList?.Id;
                ReconcileAccounts(accounts);
                ReconcileTaskListGroups(listGroups);
                ReconcileTaskLists(MergePendingLists(lists));

                if (_selectedAccountId is not { } selectedAccountId ||
                    Accounts.All(account => account.Id != selectedAccountId))
                {
                    _selectedAccountId = SelectedList is not null &&
                                         Accounts.Any(account => account.Id == SelectedList.MailAccountId)
                        ? SelectedList.MailAccountId
                        : Accounts.FirstOrDefault()?.Id;
                }

                _menuCountTasks.Clear();
                foreach (var task in MergePendingTasks(allTasks))
                    _menuCountTasks[task.Id] = task;

                _suppressSurfaceReloadDepth++;
                try
                {
                    SelectedList = selectedListId is { } id
                        ? TaskLists.FirstOrDefault(list => list.Id == id)
                        : null;
                }
                finally
                {
                    _suppressSurfaceReloadDepth--;
                }

                OnPropertyChanged(nameof(CanCreateTask));
                OnPropertyChanged(nameof(IsQuickAddVisible));
                OnPropertyChanged(nameof(CanEditSelectedTask));
                AddTaskCommand.NotifyCanExecuteChanged();
                SyncShellMenuItems();
                ApplyMenuCounts();
            }).ConfigureAwait(false);
            await ReloadTasksAsync().ConfigureAwait(false);
        }
        finally
        {
            await ExecuteUIThread(() => IsLoading = false).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task ReloadTasksAsync()
    {
        var reloadVersion = Interlocked.Increment(ref _taskReloadVersion);
        TaskReloadSnapshot snapshot = null;
        await ExecuteUIThread(() => snapshot = new TaskReloadSnapshot(
            SelectedList?.Id,
            SelectedList is null ? SelectedView : TaskViewKind.All,
            SelectedSort,
            IsMyDaySelected,
            SelectedList is null,
            _pendingTaskStates.Values.Select(RequestEntityCloner.Task).ToList(),
            _pendingDeletedTaskIds.Keys.ToHashSet())).ConfigureAwait(false);

        var tasks = await _taskService.GetTasksAsync(
            listId: snapshot.ListId,
            view: snapshot.View,
            sort: snapshot.Sort).ConfigureAwait(false) ?? [];

        var suggestions = snapshot.IsMyDaySelected
            ? await _taskService.GetMyDaySuggestionsAsync().ConfigureAwait(false) ?? []
            : [];

        await ExecuteUIThread(() =>
        {
            if (reloadVersion != Volatile.Read(ref _taskReloadVersion))
                return;

            var previousSelectionId = SelectedTask?.Id;
            var effectiveTasks = tasks
                .Where(task => !snapshot.PendingDeletedTaskIds.Contains(task.Id) &&
                               snapshot.PendingTasks.All(pending => pending.Id != task.Id))
                .Concat(snapshot.PendingTasks)
                .ToList();
            ReconcileLoadedTasks(effectiveTasks, snapshot.ShowListName);
            ReconcileSuggestions(suggestions);
            ReconcileTaskGroups();

            SelectedTask = previousSelectionId is { } id && _taskItems.TryGetValue(id, out var selected)
                && TaskGroups.Any(group => group.Contains(selected))
                    ? selected
                    : null;

            OnPropertyChanged(nameof(CanCreateTask));
            OnPropertyChanged(nameof(CanEditSelectedTask));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateBody));
        }).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanCreateTask))]
    private async Task AddTaskAsync()
    {
        var destination = SelectedList ?? GetWritableDestinationList();
        if (destination is null)
            return;

        var title = string.IsNullOrWhiteSpace(ComposerText) ? Translator.ToDoPage_NewTask : ComposerText.Trim();

        // A due date is not a My Day membership. Only the My Day surface itself pulls the
        // new task into today.
        var task = new AccountTask
        {
            MailAccountId = destination.MailAccountId,
            TaskListId = destination.Id,
            SourceKind = destination.SourceKind,
            Title = title,
            DueDate = ComposerDueDate ?? (SelectedList is null && SelectedView == TaskViewKind.Planned ? DateTime.Now.Date : null),
            IsImportant = SelectedList is null && SelectedView == TaskViewKind.Important,
            MyDayDateUtc = IsMyDaySelected ? DateTime.UtcNow.Date : null
        };

        await QueueMutationAsync(new TaskActionRequest(
            task.MailAccountId,
            TaskSynchronizerOperation.CreateTask,
            Task: task)).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            ComposerText = string.Empty;
            ComposerDueDate = null;
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly)
            return;

        var original = RequestEntityCloner.Task(item.Task);
        var desired = RequestEntityCloner.Task(item.Task);
        desired.IsCompleted = !desired.IsCompleted;
        desired.CompletedAtUtc = desired.IsCompleted ? DateTime.UtcNow : null;

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desired,
            OriginalTask: original)).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ToggleImportanceAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly)
            return;

        var original = RequestEntityCloner.Task(item.Task);
        var desired = RequestEntityCloner.Task(item.Task);
        desired.IsImportant = !desired.IsImportant;

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desired,
            OriginalTask: original)).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task AddToMyDayAsync(TaskItemViewModel item)
        => SetMyDayAsync(item, DateTime.UtcNow.Date);

    [RelayCommand]
    private Task RemoveFromMyDayAsync(TaskItemViewModel item)
        => SetMyDayAsync(item, null);

    [RelayCommand]
    private Task ToggleMyDayAsync(TaskItemViewModel item)
        => SetMyDayAsync(item, item?.IsInMyDay == true ? null : DateTime.UtcNow.Date);

    private async Task SetMyDayAsync(TaskItemViewModel item, DateTime? value)
    {
        if (item is null || item.IsReadOnly)
            return;

        var original = RequestEntityCloner.Task(item.Task);
        var desired = RequestEntityCloner.Task(item.Task);
        desired.MyDayDateUtc = value?.Date;

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desired,
            OriginalTask: original)).ConfigureAwait(false);
    }

    [RelayCommand]
    private void OpenSuggestions() => IsSuggestionsOpen = true;

    [RelayCommand]
    private void CloseSuggestions() => IsSuggestionsOpen = false;

    [RelayCommand]
    private async Task AddSuggestionToMyDayAsync(TaskItemViewModel item)
    {
        if (item is null)
            return;

        await SetMyDayAsync(item, DateTime.UtcNow.Date).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the sort as a string so the overflow menu can pass a literal CommandParameter.
    /// A typed enum parameter would arrive as a string from XAML and fail the cast.
    /// </summary>
    [RelayCommand]
    private void SetSort(string sort)
        => SelectedSort = sort switch
        {
            "importance" => TaskSortKind.Importance,
            "myday" => TaskSortKind.MyDay,
            "alphabetical" => TaskSortKind.Alphabetical,
            "created" => TaskSortKind.CreationDate,
            _ => TaskSortKind.DueDate
        };

    /// <summary>Takes the scope as a string for the same reason <see cref="SetSort"/> does.</summary>
    [RelayCommand]
    private void SetCompletionScope(string scope)
        => SelectedCompletionScope = scope switch
        {
            "completed" => TaskCompletionScope.Completed,
            "all" => TaskCompletionScope.All,
            _ => TaskCompletionScope.Active
        };

    /// <summary>Returns the surface to what it shows unfiltered. Disabled while nothing narrows it.</summary>
    [RelayCommand(CanExecute = nameof(IsFiltered))]
    private void ClearFilters()
    {
        _suppressSurfaceReloadDepth++;
        try
        {
            FilterText = string.Empty;
            SelectedCompletionScope = TaskCompletionScope.Active;
            IsImportantTasksFilterSelected = false;
        }
        finally
        {
            _suppressSurfaceReloadDepth--;
        }

        ReconcileTaskGroups();
    }

    /// <summary>Applies a relative due date from the detail drawer's preset flyout.</summary>
    [RelayCommand]
    private async Task SetDuePresetAsync(string preset)
    {
        if (SelectedTask is null || !CanEditSelectedTask)
            return;

        var today = DateTime.Now.Date;
        DateTime? due = preset switch
        {
            "today" => today,
            "tomorrow" => today.AddDays(1),
            "nextweek" => today.AddDays(7),
            _ => null
        };

        await SetTaskDueDateAsync(SelectedTask, due).ConfigureAwait(false);
    }

    public async Task SetTaskDueDateAsync(TaskItemViewModel item, DateTime? dueDate)
    {
        if (item is null || item.IsReadOnly)
            return;

        var original = RequestEntityCloner.Task(item.Task);
        var desired = RequestEntityCloner.Task(item.Task);
        desired.DueDate = dueDate?.Date;

        if (SelectedTask?.Id == item.Id)
            SelectedTaskDueDate = dueDate is { } value ? new DateTimeOffset(value.Date) : null;

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desired,
            OriginalTask: original)).ConfigureAwait(false);
    }

    public async Task MoveTaskAsync(TaskItemViewModel item, AccountTaskList destination)
    {
        if (item is null || item.IsReadOnly || destination is null || destination.IsReadOnly ||
            destination.Id == item.Task.TaskListId || destination.MailAccountId != item.Task.MailAccountId ||
            destination.SourceKind != item.Task.SourceKind)
        {
            return;
        }

        var original = RequestEntityCloner.Task(item.Task);
        var desired = RequestEntityCloner.Task(item.Task);
        desired.TaskListId = destination.Id;

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desired,
            OriginalTask: original)).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SaveTaskAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly)
            return;

        var steps = item.Steps.Select(step => step.Step).ToList();
        var desiredTask = RequestEntityCloner.Task(item.Task);
        var originalTask = item.CreateOriginalSnapshot();

        await QueueMutationAsync(new TaskActionRequest(
            desiredTask.MailAccountId,
            TaskSynchronizerOperation.UpdateTask,
            Task: desiredTask,
            OriginalTask: originalTask)).ConfigureAwait(false);

        foreach (var step in steps)
        {
            await QueueMutationAsync(new TaskActionRequest(
                step.MailAccountId,
                TaskSynchronizerOperation.UpdateStep,
                Task: desiredTask,
                Step: step,
                OriginalStep: item.Steps.First(candidate => candidate.Step.Id == step.Id).CreateOriginalSnapshot())).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly)
            return;

        await QueueMutationAsync(new TaskActionRequest(
            item.Task.MailAccountId,
            TaskSynchronizerOperation.DeleteTask,
            Task: item.Task,
            OriginalTask: item.Task)).ConfigureAwait(false);
    }

    [RelayCommand]
    private void BackToTaskList()
        => SelectedTask = null;

    [RelayCommand]
    private void CloseDetail()
        => SelectedTask = null;

    [RelayCommand]
    private async Task AddStepAsync()
    {
        if (SelectedTask is null || !CanEditSelectedTask)
            return;

        var selectedTask = SelectedTask;
        var step = new AccountTaskStep
        {
            TaskId = selectedTask.Id,
            MailAccountId = selectedTask.Task.MailAccountId,
            SourceKind = selectedTask.Task.SourceKind,
            Title = Translator.ToDoPage_NewStep,
            Order = selectedTask.Steps.Count
        };

        await QueueMutationAsync(new TaskActionRequest(
            step.MailAccountId,
            TaskSynchronizerOperation.CreateStep,
            Task: selectedTask.Task,
            Step: step)).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ToggleStepAsync(TaskStepViewModel step)
    {
        if (step is null || !CanEditSelectedTask)
            return;

        var selectedTask = SelectedTask;
        var original = RequestEntityCloner.TaskStep(step.Step);
        var desired = RequestEntityCloner.TaskStep(step.Step);
        desired.IsCompleted = !desired.IsCompleted;

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateStep,
            Task: selectedTask?.Task,
            Step: desired,
            OriginalStep: original)).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SaveStepAsync(TaskStepViewModel step)
    {
        if (step is null || !CanEditSelectedTask)
            return;

        var desired = RequestEntityCloner.TaskStep(step.Step);
        var original = step.CreateOriginalSnapshot();
        if (string.Equals(desired.Title, original.Title, StringComparison.Ordinal) &&
            desired.IsCompleted == original.IsCompleted &&
            desired.Order == original.Order)
        {
            return;
        }

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateStep,
            Task: SelectedTask?.Task,
            Step: desired,
            OriginalStep: original)).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task DeleteStepAsync(TaskStepViewModel step)
    {
        if (step is null || !CanEditSelectedTask)
            return;

        await QueueMutationAsync(new TaskActionRequest(
            step.Step.MailAccountId,
            TaskSynchronizerOperation.DeleteStep,
            Task: SelectedTask?.Task,
            Step: step.Step,
            OriginalStep: step.Step)).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task CreateListAsync()
    {
        var account = Accounts.FirstOrDefault(account => account.Id == _selectedAccountId)
            ?? Accounts.FirstOrDefault(account => account.IsTaskAccessEnabled);
        if (account is null)
            return;
        await CreateListForAccountAsync(account, null).ConfigureAwait(false);
    }

    private async Task CreateListForAccountAsync(MailAccount account, Guid? groupId)
    {
        var now = DateTime.UtcNow;
        var list = new AccountTaskList
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            SourceKind = ResolveTaskSource(account),
            Title = Translator.ToDoPage_NewList,
            ColorHex = ColorPalette.GetDistinctColor(TaskLists.Select(existing => existing.ColorHex)),
            GroupId = groupId,
            SortOrder = TaskLists.Count(existing => existing.MailAccountId == account.Id && existing.GroupId == groupId),
            CreatedAtUtc = now,
            ModifiedAtUtc = now
        };

        await QueueMutationAsync(new TaskActionRequest(
            account.Id,
            TaskSynchronizerOperation.CreateList,
            List: list)).ConfigureAwait(false);
    }

    private async Task CreateGroupAsync()
    {
        var account = Accounts.FirstOrDefault(candidate => candidate.Id == _selectedAccountId)
            ?? (SelectedList is null
                ? Accounts.FirstOrDefault(candidate => candidate.IsTaskAccessEnabled)
                : Accounts.FirstOrDefault(candidate => candidate.Id == SelectedList.MailAccountId));
        if (account is null || _taskMutationService is null)
            return;

        var title = await _dialogService.ShowTextInputDialogAsync(
            string.Empty,
            Translator.ToDoPage_NewGroup,
            Translator.ToDoPage_GroupNamePrompt,
            Translator.Buttons_Create).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title))
            return;

        var now = DateTime.UtcNow;
        var sourceKind = ResolveTaskSource(account) == TaskSourceKind.Outlook
            ? TaskSourceKind.Outlook
            : TaskSourceKind.Local;
        var group = new AccountTaskListGroup
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            SourceKind = sourceKind,
            Title = title.Trim(),
            SortOrder = TaskListGroups.Count(item => item.MailAccountId == account.Id),
            RemoteOrder = NextRemoteOrder(TaskListGroups.Where(item => item.MailAccountId == account.Id)
                .Select(item => item.RemoteOrder)).ToString("O"),
            PendingMutation = sourceKind == TaskSourceKind.Outlook ? TaskPendingMutation.Create : TaskPendingMutation.None,
            CreatedAtUtc = now,
            ModifiedAtUtc = now
        };
        await QueueMutationAsync(new TaskActionRequest(
            account.Id,
            TaskSynchronizerOperation.CreateGroup,
            Group: group)).ConfigureAwait(false);
    }

    private Task CreateListInGroupAsync(AccountTaskListGroupMenuItem group)
    {
        var account = Accounts.FirstOrDefault(candidate => candidate.Id == group.Parameter.MailAccountId);
        return account is null ? Task.CompletedTask : CreateListForAccountAsync(account, group.Parameter.Id);
    }

    private async Task RenameGroupAsync(AccountTaskListGroupMenuItem item)
    {
        if (_taskMutationService is null || !item.IsEditable)
            return;
        var title = await _dialogService.ShowTextInputDialogAsync(
            item.Title,
            Translator.ToDoPage_RenameGroup,
            Translator.ToDoPage_GroupNamePrompt,
            Translator.FolderOperation_Rename).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title) || string.Equals(title.Trim(), item.Title, StringComparison.Ordinal))
            return;

        var original = RequestEntityCloner.TaskListGroup(item.Parameter);
        var desired = RequestEntityCloner.TaskListGroup(item.Parameter);
        desired.Title = title.Trim();
        desired.PendingMutation = desired.SourceKind == TaskSourceKind.Outlook ? TaskPendingMutation.Update : TaskPendingMutation.None;
        desired.ModifiedAtUtc = DateTime.UtcNow;
        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateGroup,
            Group: desired,
            OriginalGroup: original)).ConfigureAwait(false);
    }

    private async Task UngroupListsAsync(AccountTaskListGroupMenuItem item)
    {
        if (_taskMutationService is null || !item.IsEditable)
            return;

        var moved = TaskLists.Where(list => list.GroupId == item.Parameter.Id).ToList();
        var nextOrder = TaskLists.Count(list => list.MailAccountId == item.Parameter.MailAccountId && list.GroupId is null);
        foreach (var list in moved)
        {
            await QueueListPlacementAsync(list, null, nextOrder++).ConfigureAwait(false);
        }
        await QueueMutationAsync(new TaskActionRequest(
            item.Parameter.MailAccountId,
            TaskSynchronizerOperation.DeleteGroup,
            Group: item.Parameter,
            OriginalGroup: item.Parameter)).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes an empty group. The flyout only offers this once the group holds no lists — a
    /// group that still has some is emptied with "ungroup lists" instead — and the emptiness is
    /// re-checked here against storage, not the menu, so a stale pane cannot delete lists.
    /// </summary>
    private async Task DeleteGroupAsync(AccountTaskListGroupMenuItem item)
    {
        if (_taskMutationService is null || !item.IsEditable)
            return;

        if (TaskLists.Any(list => list.GroupId == item.Parameter.Id))
            return;

        await QueueMutationAsync(new TaskActionRequest(
            item.Parameter.MailAccountId,
            TaskSynchronizerOperation.DeleteGroup,
            Group: item.Parameter,
            OriginalGroup: item.Parameter)).ConfigureAwait(false);
    }

    private async Task RenameListFromMenuAsync(AccountTaskListMenuItem item)
    {
        if (item.Parameter.IsReadOnly)
            return;
        var title = await _dialogService.ShowTextInputDialogAsync(
            item.Title,
            Translator.ToDoPage_RenameList,
            Translator.ToDoPage_ListNamePrompt,
            Translator.FolderOperation_Rename).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(title))
            return;

        var original = RequestEntityCloner.TaskList(item.Parameter);
        var desired = RequestEntityCloner.TaskList(item.Parameter);
        desired.Title = title.Trim();
        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateList,
            List: desired,
            OriginalList: original)).ConfigureAwait(false);
    }

    private Task RemoveListFromGroupAsync(AccountTaskListMenuItem item)
        => MoveListAsync(item.Parameter, null, int.MaxValue);

    private Task MoveListToGroupAsync(AccountTaskListMenuItem item, Guid groupId)
        => item.CanMoveToGroup
            ? MoveListAsync(item.Parameter, groupId, int.MaxValue)
            : Task.CompletedTask;

    private async Task DeleteListFromMenuAsync(AccountTaskListMenuItem item)
    {
        if (!item.CanDelete)
            return;
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            Translator.ToDoPage_DeleteListConfirmBody,
            string.Format(Translator.ToDoPage_DeleteListConfirmTitle, item.Title),
            Translator.ToDoPage_DeleteList).ConfigureAwait(false);
        if (!confirmed)
            return;
        await QueueMutationAsync(new TaskActionRequest(
            item.Parameter.MailAccountId,
            TaskSynchronizerOperation.DeleteList,
            List: item.Parameter,
            OriginalList: item.Parameter)).ConfigureAwait(false);
    }

    private async Task HandleShellDropAsync(IMenuItem source, IMenuItem target, bool insertAfter)
    {
        if (_taskMutationService is null)
            return;
        if (source is AccountTaskListGroupMenuItem sourceGroup && target is AccountTaskListGroupMenuItem targetGroup)
        {
            await ReorderGroupAsync(sourceGroup.Parameter, targetGroup.Parameter, insertAfter).ConfigureAwait(false);
            return;
        }
        if (source is not AccountTaskListMenuItem sourceList)
            return;

        if (target is AccountTaskListGroupMenuItem destinationGroup)
        {
            if (!sourceList.CanMoveToGroup)
                return;

            await MoveListAsync(sourceList.Parameter, destinationGroup.Parameter.Id, int.MaxValue).ConfigureAwait(false);
        }
        else if (target is AccountTaskListMenuItem destinationList)
        {
            if (!sourceList.CanMoveToGroup && destinationList.Parameter.GroupId is not null)
                return;

            var siblings = TaskLists.Where(list => list.MailAccountId == destinationList.Parameter.MailAccountId && list.GroupId == destinationList.Parameter.GroupId)
                .Where(list => list.Id != sourceList.Parameter.Id)
                .OrderBy(list => list.SortOrder).ThenBy(list => list.Title, StringComparer.OrdinalIgnoreCase).ToList();
            await MoveListAsync(sourceList.Parameter, destinationList.Parameter.GroupId,
                siblings.IndexOf(destinationList.Parameter) + (insertAfter ? 1 : 0)).ConfigureAwait(false);
        }
    }

    private async Task MoveListAsync(AccountTaskList list, Guid? destinationGroupId, int destinationIndex)
    {
        if (_taskMutationService is null || list.IsOutlookDefaultList && destinationGroupId is not null)
            return;
        var originalPlacements = TaskLists.ToDictionary(
            candidate => candidate.Id,
            candidate => (candidate.GroupId, candidate.SortOrder, candidate.RemoteOrder));
        var oldGroupId = list.GroupId;
        var destination = TaskLists.Where(candidate => candidate.MailAccountId == list.MailAccountId && candidate.GroupId == destinationGroupId && candidate.Id != list.Id)
            .OrderBy(candidate => candidate.SortOrder).ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase).ToList();
        destinationIndex = Math.Clamp(destinationIndex, 0, destination.Count);
        destination.Insert(destinationIndex, list);
        list.GroupId = destinationGroupId;

        for (var index = 0; index < destination.Count; index++)
            destination[index].SortOrder = index;
        if (oldGroupId != destinationGroupId)
        {
            var oldSiblings = TaskLists.Where(candidate => candidate.MailAccountId == list.MailAccountId && candidate.GroupId == oldGroupId && candidate.Id != list.Id)
                .OrderBy(candidate => candidate.SortOrder).ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase).ToList();
            for (var index = 0; index < oldSiblings.Count; index++)
                oldSiblings[index].SortOrder = index;
        }

        var movedIndex = destination.IndexOf(list);
        var midpoint = TryGetRemoteOrderBetween(
            movedIndex > 0 ? destination[movedIndex - 1].RemoteOrder : null,
            movedIndex + 1 < destination.Count ? destination[movedIndex + 1].RemoteOrder : null);
        if (midpoint.HasValue)
        {
            list.RemoteOrder = midpoint.Value.ToString("O");
        }
        else
        {
            var baseOrder = DateTimeOffset.UtcNow;
            for (var index = 0; index < destination.Count; index++)
                destination[index].RemoteOrder = baseOrder.AddSeconds(index).ToString("O");
        }

        // Reflect the hierarchy move before any provider request can yield. Both drag/drop and
        // the context command arrive here, so they share the same immediate presentation path.
        SyncShellMenuItems();

        var requests = new List<TaskActionRequest>();
        foreach (var affectedList in TaskLists.Where(candidate =>
                     !originalPlacements.TryGetValue(candidate.Id, out var original) ||
                     original.GroupId != candidate.GroupId ||
                     original.SortOrder != candidate.SortOrder ||
                     !string.Equals(original.RemoteOrder, candidate.RemoteOrder, StringComparison.Ordinal)))
        {
            var request = await CreateListPlacementRequestAsync(
                affectedList,
                affectedList.GroupId,
                affectedList.SortOrder).ConfigureAwait(false);
            if (request is not null)
                requests.Add(request);
        }

        if (requests.Count > 0)
            await _requestDelegator.ExecuteAsync(list.MailAccountId, requests).ConfigureAwait(false);
    }

    private async Task QueueListPlacementAsync(AccountTaskList list, Guid? groupId, int sortOrder)
    {
        var request = await CreateListPlacementRequestAsync(list, groupId, sortOrder).ConfigureAwait(false);
        if (request is not null)
            await QueueMutationAsync(request).ConfigureAwait(false);
    }

    private async Task<TaskActionRequest> CreateListPlacementRequestAsync(AccountTaskList list, Guid? groupId, int sortOrder)
    {
        if (list.IsOutlookDefaultList && groupId is not null)
            return null;

        var original = await _taskMutationService.GetTaskListAsync(list.Id).ConfigureAwait(false)
            ?? RequestEntityCloner.TaskList(list);
        var desired = RequestEntityCloner.TaskList(list);
        desired.GroupId = groupId;
        desired.SortOrder = sortOrder;
        desired.RemoteOrder ??= DateTimeOffset.UtcNow.AddSeconds(sortOrder).ToString("O");
        desired.PendingMutation = desired.SourceKind == TaskSourceKind.Outlook ? TaskPendingMutation.Update : TaskPendingMutation.None;
        desired.ModifiedAtUtc = DateTime.UtcNow;
        return new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateListPlacement,
            List: desired,
            OriginalList: original);
    }

    private async Task ReorderGroupAsync(AccountTaskListGroup source, AccountTaskListGroup target, bool insertAfter)
    {
        var groups = TaskListGroups.Where(group => group.MailAccountId == source.MailAccountId)
            .OrderBy(group => group.SortOrder).ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase).ToList();
        groups.Remove(source);
        groups.Insert(Math.Max(0, groups.IndexOf(target) + (insertAfter ? 1 : 0)), source);
        var sourceIndex = groups.IndexOf(source);
        var midpoint = TryGetRemoteOrderBetween(
            sourceIndex > 0 ? groups[sourceIndex - 1].RemoteOrder : null,
            sourceIndex + 1 < groups.Count ? groups[sourceIndex + 1].RemoteOrder : null);
        if (midpoint.HasValue)
        {
            source.RemoteOrder = midpoint.Value.ToString("O");
        }
        else
        {
            var baseOrder = DateTimeOffset.UtcNow;
            for (var index = 0; index < groups.Count; index++)
                groups[index].RemoteOrder = baseOrder.AddSeconds(index).ToString("O");
        }
        for (var index = 0; index < groups.Count; index++)
        {
            var original = await _taskMutationService.GetTaskListGroupsAsync(groups[index].MailAccountId).ConfigureAwait(false) ?? [];
            var originalGroup = RequestEntityCloner.TaskListGroup(
                original.FirstOrDefault(item => item.Id == groups[index].Id) ?? groups[index]);
            groups[index].SortOrder = index;
            var desired = RequestEntityCloner.TaskListGroup(groups[index]);
            desired.PendingMutation = desired.SourceKind == TaskSourceKind.Outlook ? TaskPendingMutation.Update : TaskPendingMutation.None;
            await QueueMutationAsync(new TaskActionRequest(
                desired.MailAccountId,
                TaskSynchronizerOperation.UpdateGroup,
                Group: desired,
                OriginalGroup: originalGroup)).ConfigureAwait(false);
        }
        await ExecuteUIThread(() =>
        {
            ReconcileTaskListGroups(groups);
            SyncShellMenuItems();
        }).ConfigureAwait(false);
    }

    private static DateTimeOffset NextRemoteOrder(IEnumerable<string> values)
    {
        var latest = values.Select(ParseRemoteOrder).Where(value => value.HasValue)
            .Select(value => value.Value).DefaultIfEmpty(DateTimeOffset.UtcNow).Max();
        return latest.AddSeconds(1);
    }

    private static DateTimeOffset? TryGetRemoteOrderBetween(string previousValue, string nextValue)
    {
        var previous = ParseRemoteOrder(previousValue);
        var next = ParseRemoteOrder(nextValue);
        if (previous.HasValue && next.HasValue)
        {
            var previousTicks = previous.Value.UtcTicks;
            var nextTicks = next.Value.UtcTicks;
            return nextTicks - previousTicks > 1
                ? new DateTimeOffset(previousTicks + ((nextTicks - previousTicks) / 2), TimeSpan.Zero)
                : null;
        }
        if (previous.HasValue)
            return previous.Value.AddSeconds(1);
        if (next.HasValue)
            return next.Value.AddSeconds(-1);
        return DateTimeOffset.UtcNow;
    }

    private static DateTimeOffset? ParseRemoteOrder(string value)
        => DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;

    [RelayCommand]
    private async Task DeleteListAsync()
    {
        if (!CanDeleteSelectedList)
            return;

        var list = SelectedList;

        // Deleting a list removes its tasks on every signed-in device and cannot be undone,
        // so it is confirmed rather than executed on a single click.
        var confirmed = await _dialogService.ShowConfirmationDialogAsync(
            Translator.ToDoPage_DeleteListConfirmBody,
            string.Format(Translator.ToDoPage_DeleteListConfirmTitle, list.Title),
            Translator.ToDoPage_DeleteList).ConfigureAwait(false);

        if (!confirmed)
            return;

        await QueueMutationAsync(new TaskActionRequest(
            list.MailAccountId,
            TaskSynchronizerOperation.DeleteList,
            List: list,
            OriginalList: list)).ConfigureAwait(false);
    }

    /// <summary>
    /// Renames the selected list through the same dialog the shell menu uses, so there is one
    /// rename experience wherever a list is renamed from.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditSelectedList))]
    private async Task RenameSelectedListAsync()
    {
        if (SelectedList is null || SelectedList.IsReadOnly)
            return;

        var title = await _dialogService.ShowTextInputDialogAsync(
            SelectedList.Title,
            Translator.ToDoPage_RenameList,
            Translator.ToDoPage_ListNamePrompt,
            Translator.FolderOperation_Rename).ConfigureAwait(false);

        await RenameListAsync(title).ConfigureAwait(false);
    }

    /// <summary>Commits a new list title. Ignores an unchanged or empty name.</summary>
    [RelayCommand]
    private async Task RenameListAsync(string title)
    {
        if (SelectedList is null || SelectedList.IsReadOnly)
            return;

        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == SelectedList.Title)
            return;

        var original = RequestEntityCloner.TaskList(SelectedList);
        var desired = RequestEntityCloner.TaskList(SelectedList);
        desired.Title = trimmed;

        await QueueMutationAsync(new TaskActionRequest(
            desired.MailAccountId,
            TaskSynchronizerOperation.UpdateList,
            List: desired,
            OriginalList: original)).ConfigureAwait(false);
    }

    /// <summary>
    /// Placeholder command. The menu entry exists so the capability is discoverable and
    /// accessible, but there is no print pipeline behind it yet.
    /// </summary>
    [RelayCommand]
    private void PrintList()
        => _dialogService.InfoBarMessage(Translator.ToDoPage_PrintList, Translator.ToDoPage_NotAvailableYet, InfoBarMessageType.Information);

    /// <summary>Placeholder command. See <see cref="PrintList"/>.</summary>
    [RelayCommand]
    private void EmailList()
        => _dialogService.InfoBarMessage(Translator.ToDoPage_EmailList, Translator.ToDoPage_NotAvailableYet, InfoBarMessageType.Information);

    [RelayCommand]
    private void Synchronize()
    {
        foreach (var account in Accounts.Where(account =>
                     account.ProviderType is (MailProviderType.Gmail or MailProviderType.Outlook) &&
                     account.IsTaskAccessGranted && !account.IsTaskReauthorizationRequired))
        {
            WeakReferenceMessenger.Default.Send(new Wino.Messaging.Server.NewTaskSynchronizationRequested(new TaskSynchronizationOptions
            {
                AccountId = account.Id,
                Type = TaskSynchronizationType.Delta
            }));
        }
    }

    [RelayCommand]
    private async Task CreateCalendarEventAsync()
    {
        var item = SelectedTask;
        if (item is null)
            return;
        var calendars = await _calendarService.GetAccountCalendarsAsync(item.Task.MailAccountId).ConfigureAwait(false);
        var calendar = calendars.FirstOrDefault(entry => !entry.IsReadOnly);
        if (calendar is null)
            return;
        var title = item.Title ?? string.Empty;
        var notes = string.IsNullOrWhiteSpace(item.Notes) ? string.Empty : $"<p>{WebUtility.HtmlEncode(item.Notes).Replace("\r\n", "<br/>").Replace("\n", "<br/>")}</p>";
        var args = new CalendarEventComposeNavigationArgs
        {
            SelectedCalendarId = calendar.Id,
            Title = title,
            NotesHtml = notes,
            IsAllDay = item.DueDate.HasValue,
            StartDate = item.DueDate ?? DateTime.Now,
            EndDate = item.DueDate?.AddDays(1) ?? DateTime.Now.AddHours(1)
        };
        await ExecuteUIThread(() =>
            _navigationService.ChangeApplicationMode(WinoApplicationMode.Calendar, new ShellModeActivationContext { Parameter = args }))
            .ConfigureAwait(false);
    }

    private async Task RefreshCalendarEventAvailabilityAsync(TaskItemViewModel item)
    {
        if (item is null)
        {
            await ExecuteUIThread(() => CanCreateCalendarEvent = false).ConfigureAwait(false);
            return;
        }

        var calendars = await _calendarService.GetAccountCalendarsAsync(item.Task.MailAccountId).ConfigureAwait(false);
        var canCreate = calendars.Any(calendar => !calendar.IsReadOnly);
        await ExecuteUIThread(() =>
        {
            if (SelectedTask?.Id == item.Id)
                CanCreateCalendarEvent = canCreate;
        }).ConfigureAwait(false);
    }

    private async Task QueueMutationAsync(TaskActionRequest request)
        => await _requestDelegator.ExecuteAsync(request.MailAccountId, [request]).ConfigureAwait(false);

    private static TaskSourceKind ResolveTaskSource(MailAccount account)
        => account.TaskIntegrationSource == AccountIntegrationSource.Provider && account.IsTaskAccessGranted
            ? account.ProviderType switch
            {
                MailProviderType.Gmail => TaskSourceKind.Gmail,
                MailProviderType.Outlook => TaskSourceKind.Outlook,
                _ => TaskSourceKind.Local
            }
            : TaskSourceKind.Local;

    private AccountTaskList GetWritableDestinationList()
        => TaskLists
            .Where(list => !list.IsReadOnly)
            .OrderByDescending(list => list.IsDefault)
            .ThenBy(list => list.MailAccountId)
            .ThenBy(list => list.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(list => list.Id)
            .FirstOrDefault();

    private static bool RequiresLocalFallbackList(MailAccount account)
        => account.IsTaskAccessEnabled &&
           (account.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook) ||
           !account.IsTaskAccessGranted ||
           account.IsTaskReauthorizationRequired);

    private string ResolveListName(AccountTask task)
        => TaskLists.FirstOrDefault(list => list.Id == task.TaskListId)?.Title ?? string.Empty;

    private void SelectSurface(TaskViewKind view, AccountTaskList list)
    {
        if (SelectedList?.Id == list?.Id && (list is not null || SelectedView == view))
            return;

        _suppressSurfaceReloadDepth++;
        try
        {
            SelectedList = list;
            SelectedView = list is null ? view : TaskViewKind.All;

            // A list can carry the sort and completed-task visibility the provider stores for it.
            // Without one, the current choice carries over as before.
            if (list?.SortKind is { } listSort)
                SelectedSort = listSort;

            if (list is not null)
            {
                SelectedCompletionScope = TaskCompletionScope.Active;
                IsImportantTasksFilterSelected = false;
            }
        }
        finally
        {
            _suppressSurfaceReloadDepth--;
        }

        UpdateSelectedMenuItemReference();
        _ = ReloadTasksAsync();
    }

    private void ReconcileLoadedTasks(IReadOnlyList<AccountTask> tasks, bool showListName)
    {
        var desiredIds = new HashSet<Guid>();
        _taskOrder.Clear();

        foreach (var task in tasks.Where(TaskMatchesCurrentSurface))
        {
            desiredIds.Add(task.Id);
            _taskOrder.Add(task.Id);

            if (!_taskItems.TryGetValue(task.Id, out var item))
            {
                item = new TaskItemViewModel(task, ResolveListName(task));
                _taskItems.Add(task.Id, item);
            }
            else
            {
                item.ApplySnapshot(task);
                item.ListName = ResolveListName(task);
            }

            item.ShowListName = showListName;
        }

        foreach (var taskId in _taskItems.Keys.Where(taskId => !desiredIds.Contains(taskId)).ToList())
            _taskItems.Remove(taskId);
    }

    private void ReconcileSuggestions(IReadOnlyList<AccountTask> suggestions)
    {
        for (var targetIndex = 0; targetIndex < suggestions.Count; targetIndex++)
        {
            var task = suggestions[targetIndex];
            var currentIndex = Suggestions.IndexOf(Suggestions.FirstOrDefault(item => item.Id == task.Id));
            if (currentIndex < 0)
            {
                Suggestions.Insert(targetIndex, new TaskItemViewModel(task, ResolveListName(task)) { ShowListName = true });
                continue;
            }

            if (currentIndex != targetIndex)
                Suggestions.Move(currentIndex, targetIndex);

            Suggestions[targetIndex].ApplySnapshot(task);
            Suggestions[targetIndex].ListName = ResolveListName(task);
            Suggestions[targetIndex].ShowListName = true;
        }

        while (Suggestions.Count > suggestions.Count)
            Suggestions.RemoveAt(Suggestions.Count - 1);
    }

    private bool TaskMatchesCurrentSurface(AccountTask task)
    {
        if (task is null)
            return false;

        if (SelectedList is not null)
            return task.TaskListId == SelectedList.Id;

        if (task.IsCompleted)
            return false;

        return SelectedView switch
        {
            TaskViewKind.MyDay => task.MyDayDateUtc == DateTime.UtcNow.Date,
            TaskViewKind.Planned => task.DueDate.HasValue,
            TaskViewKind.Important => task.IsImportant,
            _ => false
        };
    }

    private bool MatchesFilter(AccountTask task)
    {
        if (string.IsNullOrWhiteSpace(FilterText))
            return true;

        var query = FilterText.Trim();
        return task.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
               task.Notes?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    private void ReconcileTaskGroups()
    {
        var visible = OrderVisibleTasks(_taskItems.Values.Where(IsTaskVisible)).ToList();
        var desiredGroups = SelectedList is not null
            ? visible.Count == 0
                ? []
                : new List<DesiredTaskGroup> { new(string.Empty, null, false, visible) }
            : visible
                .GroupBy(item => item.Task.MailAccountId)
                .OrderBy(group => GetAccountOrder(group.Key))
                .ThenBy(group => GetAccountName(group.Key), StringComparer.OrdinalIgnoreCase)
                .Select(group => new DesiredTaskGroup(
                    GetAccountName(group.Key),
                    group.Key,
                    true,
                    group.ToList()))
                .ToList();

        for (var targetIndex = 0; targetIndex < desiredGroups.Count; targetIndex++)
        {
            var desired = desiredGroups[targetIndex];
            var group = _taskGroups.FirstOrDefault(item => item.AccountId == desired.AccountId && item.ShowHeader == desired.ShowHeader);
            if (group is null)
            {
                group = new TaskGroup(desired.Key, desired.AccountId, desired.ShowHeader);
                _taskGroups.Insert(targetIndex, group);
            }
            else
            {
                group.UpdateHeader(desired.Key);
                var currentIndex = _taskGroups.IndexOf(group);
                if (currentIndex != targetIndex)
                    _taskGroups.Move(currentIndex, targetIndex);
            }

            ReconcileTaskGroupItems(group, desired.Items);
        }

        while (_taskGroups.Count > desiredGroups.Count)
            _taskGroups.RemoveAt(_taskGroups.Count - 1);

        if (SelectedTask is not null && !_taskGroups.Any(group => group.Contains(SelectedTask)))
            SelectedTask = null;

        OnPropertyChanged(nameof(IsEmpty));

        // The subtitle reports the counts, so it is only correct once the groups are.
        OnPropertyChanged(nameof(SelectedSurfaceSubtitle));
        OnPropertyChanged(nameof(HasSurfaceSubtitle));
    }

    /// <summary>
    /// Applies to every surface, not just named lists. A smart view holds no completed tasks,
    /// so the completion scope simply never excludes anything there.
    /// </summary>
    private bool IsTaskVisible(TaskItemViewModel item)
    {
        if (!MatchesFilter(item.Task))
            return false;

        if (IsImportantTasksFilterSelected && !item.IsImportant)
            return false;

        return SelectedCompletionScope switch
        {
            TaskCompletionScope.Active => !item.IsCompleted,
            TaskCompletionScope.Completed => item.IsCompleted,
            _ => true
        };
    }

    private IEnumerable<TaskItemViewModel> OrderVisibleTasks(IEnumerable<TaskItemViewModel> items)
    {
        var ordered = items.OrderBy(item => item.IsCompleted);
        ordered = SelectedSort switch
        {
            TaskSortKind.Importance => ordered.ThenByDescending(item => item.IsImportant).ThenBy(item => item.DueDate ?? DateTime.MaxValue),
            TaskSortKind.MyDay => ordered.ThenByDescending(item => item.MyDayDateUtc ?? DateTime.MinValue).ThenBy(item => item.DueDate ?? DateTime.MaxValue),
            TaskSortKind.Alphabetical => ordered.ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase),
            TaskSortKind.CreationDate => ordered.ThenByDescending(item => item.Task.CreatedAtUtc),
            _ => ordered.ThenBy(item => item.DueDate ?? DateTime.MaxValue)
        };

        return ordered
            .ThenBy(item => item.Task.ModifiedAtUtc)
            .ThenBy(item => _taskOrder.IndexOf(item.Id));
    }

    private static void ReconcileTaskGroupItems(TaskGroup group, IReadOnlyList<TaskItemViewModel> desired)
    {
        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var desiredItem = desired[targetIndex];
            var currentIndex = group.IndexOf(group.FirstOrDefault(item => item.Id == desiredItem.Id));
            if (currentIndex < 0)
            {
                group.Insert(targetIndex, desiredItem);
                continue;
            }

            if (currentIndex != targetIndex)
                group.Move(currentIndex, targetIndex);

            if (!ReferenceEquals(group[targetIndex], desiredItem))
                group[targetIndex] = desiredItem;
        }

        while (group.Count > desired.Count)
            group.RemoveAt(group.Count - 1);
    }

    private void ReconcileAccounts(IReadOnlyList<MailAccount> accounts)
    {
        var desired = accounts.OrderBy(account => account.Order).ThenBy(account => account.Name, StringComparer.OrdinalIgnoreCase).ToList();
        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var account = desired[targetIndex];
            var currentIndex = Accounts.IndexOf(Accounts.FirstOrDefault(item => item.Id == account.Id));
            if (currentIndex < 0)
            {
                Accounts.Insert(targetIndex, account);
                continue;
            }

            if (currentIndex != targetIndex)
                Accounts.Move(currentIndex, targetIndex);
            Accounts[targetIndex] = account;

            if (_accountGroupMenuItems.TryGetValue(account.Id, out var header))
                header.UpdateAccount(account);
        }

        while (Accounts.Count > desired.Count)
            Accounts.RemoveAt(Accounts.Count - 1);
    }

    private void ReconcileTaskLists(IReadOnlyList<AccountTaskList> lists)
    {
        var desired = lists
            .OrderBy(list => GetAccountOrder(list.MailAccountId))
            .ThenBy(list => list.GroupId.HasValue)
            .ThenBy(list => list.SortOrder)
            .ThenByDescending(list => list.IsDefault)
            .ThenBy(list => list.Id)
            .ToList();

        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var list = desired[targetIndex];
            var currentIndex = TaskLists.IndexOf(TaskLists.FirstOrDefault(item => item.Id == list.Id));
            if (currentIndex < 0)
            {
                TaskLists.Insert(targetIndex, list);
                continue;
            }

            if (currentIndex != targetIndex)
                TaskLists.Move(currentIndex, targetIndex);
            TaskLists[targetIndex] = list;
        }

        while (TaskLists.Count > desired.Count)
            TaskLists.RemoveAt(TaskLists.Count - 1);
    }

    private void ReconcileTaskListGroups(IReadOnlyList<AccountTaskListGroup> groups)
    {
        var desired = groups
            .OrderBy(group => GetAccountOrder(group.MailAccountId))
            .ThenBy(group => group.SortOrder)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Id)
            .ToList();

        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var group = desired[targetIndex];
            var currentIndex = TaskListGroups.IndexOf(TaskListGroups.FirstOrDefault(item => item.Id == group.Id));
            if (currentIndex < 0)
                TaskListGroups.Insert(targetIndex, group);
            else
            {
                if (currentIndex != targetIndex)
                    TaskListGroups.Move(currentIndex, targetIndex);
                TaskListGroups[targetIndex] = group;
            }
        }

        while (TaskListGroups.Count > desired.Count)
            TaskListGroups.RemoveAt(TaskListGroups.Count - 1);
    }

    private void SyncShellMenuItems()
    {
        if (ShellMenu?.Items is null)
            return;

        var desired = new List<IMenuItem>(GetShellMenuPrefix());
        var prefixCount = desired.Count;
        var activeAccountIds = new HashSet<Guid>();
        var activeListIds = new HashSet<Guid>();
        IReadOnlyList<IMenuItem> selectedAccountChildren = [];

        foreach (var account in Accounts.OrderBy(account => account.Order).ThenBy(account => account.Name, StringComparer.OrdinalIgnoreCase))
        {
            var accountLists = TaskLists.Where(list => list.MailAccountId == account.Id).ToList();
            // Ordering never keys on Title: a rename would move the item, and a moved item makes
            // NavigationView rebuild its container and drop the selection.
            var accountGroups = TaskListGroups.Where(group => group.MailAccountId == account.Id)
                .OrderBy(group => group.SortOrder)
                .ThenBy(group => group.Id)
                .ToList();
            activeAccountIds.Add(account.Id);
            if (!_accountGroupMenuItems.TryGetValue(account.Id, out var header))
            {
                header = new AccountTaskListAccountMenuItem(account);
                _accountGroupMenuItems.Add(account.Id, header);
            }
            else
            {
                header.UpdateAccount(account);
            }

            var accountChildren = new List<IMenuItem>(accountGroups.Count + accountLists.Count);

            foreach (var group in accountGroups)
            {
                if (!_taskListGroupMenuItems.TryGetValue(group.Id, out var groupItem))
                {
                    groupItem = new AccountTaskListGroupMenuItem(group)
                    {
                        NewListRequested = CreateListInGroupAsync,
                        RenameRequested = RenameGroupAsync,
                        UngroupRequested = UngroupListsAsync,
                        DeleteRequested = DeleteGroupAsync,
                        DropRequested = HandleShellDropAsync
                    };
                    groupItem.PropertyChanged += TaskListGroupMenuItem_PropertyChanged;
                    _taskListGroupMenuItems.Add(group.Id, groupItem);
                }
                else
                {
                    groupItem.Update(group);
                }

                var childItems = accountLists
                    .Where(list => list.GroupId == group.Id)
                    .OrderBy(list => list.SortOrder)
                    .ThenBy(list => list.Id)
                    .Select(list => GetOrCreateListMenuItem(list, account, accountGroups, activeListIds))
                    .Cast<IMenuItem>()
                    .ToList();
                ApplyDesiredSubMenuItems(groupItem, childItems);
                accountChildren.Add(groupItem);
            }

            foreach (var list in accountLists.Where(list => list.GroupId is null)
                         .OrderBy(list => list.SortOrder)
                         .ThenByDescending(list => list.IsDefault)
                         .ThenBy(list => list.Id))
            {
                accountChildren.Add(GetOrCreateListMenuItem(list, account, accountGroups, activeListIds));
            }

            ApplyDesiredSubMenuItems(header.SubMenuItems, accountChildren);
            header.IsSelected = account.Id == _selectedAccountId;
            desired.Add(header);

            if (header.IsSelected)
                selectedAccountChildren = accountChildren;
        }

        foreach (var accountId in _accountGroupMenuItems.Keys.Where(id => !activeAccountIds.Contains(id)).ToList())
            _accountGroupMenuItems.Remove(accountId);
        foreach (var listId in _listMenuItems.Keys.Where(id => !activeListIds.Contains(id)).ToList())
            _listMenuItems.Remove(listId);
        var activeGroupIds = TaskListGroups.Select(group => group.Id).ToHashSet();
        foreach (var groupId in _taskListGroupMenuItems.Keys.Where(id => !activeGroupIds.Contains(id)).ToList())
        {
            _taskListGroupMenuItems[groupId].PropertyChanged -= TaskListGroupMenuItem_PropertyChanged;
            _taskListGroupMenuItems.Remove(groupId);
        }

        // Account selection is independent from NavigationView's selected smart view/list. The
        // custom account item renders this second selection while the selected account's task
        // hierarchy is projected as the only list section below all account rows.
        if (desired.Count > prefixCount)
        {
            desired.Insert(prefixCount, _smartViewSeparator);
            if (selectedAccountChildren.Count > 0)
            {
                desired.Add(_accountSeparator);
                desired.AddRange(selectedAccountChildren);
            }
        }

        ApplyDesiredMenuItems(desired);
        UpdateSelectedMenuItemReference();
    }

    /// <summary>
    /// The fixed head of the pane: the commands, then the smart views. Held in one place so the
    /// initial seed and every later reconcile agree on it.
    /// </summary>
    private IMenuItem[] GetShellMenuPrefix() =>
    [
        _newListMenuItem,
        _taskSyncMenuItem,
        _commandSeparator,
        _myDayMenuItem,
        _plannedMenuItem,
        _importantMenuItem
    ];

    private AccountTaskListMenuItem GetOrCreateListMenuItem(
        AccountTaskList list,
        MailAccount account,
        IReadOnlyList<AccountTaskListGroup> accountGroups,
        ISet<Guid> activeListIds)
    {
        activeListIds.Add(list.Id);
        if (!_listMenuItems.TryGetValue(list.Id, out var item))
        {
            item = new AccountTaskListMenuItem(list, account.Name)
            {
                RenameRequested = RenameListFromMenuAsync,
                RemoveFromGroupRequested = RemoveListFromGroupAsync,
                MoveToGroupRequested = MoveListToGroupAsync,
                DeleteRequested = DeleteListFromMenuAsync,
                DropRequested = HandleShellDropAsync
            };
            _listMenuItems.Add(list.Id, item);
        }

        item.Update(list, account.Name, accountGroups);
        return item;
    }

    private static void ApplyDesiredSubMenuItems(AccountTaskListGroupMenuItem group, IReadOnlyList<IMenuItem> desired)
    {
        ApplyDesiredSubMenuItems(group.SubMenuItems, desired);

        // Emptying or filling a group flips whether it can be deleted.
        group.NotifyChildrenChanged();
    }

    private static void ApplyDesiredSubMenuItems(ObservableCollection<IMenuItem> items, IReadOnlyList<IMenuItem> desired)
    {
        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var item = desired[targetIndex];
            var currentIndex = items.IndexOf(item);
            if (currentIndex < 0)
                items.Insert(targetIndex, item);
            else if (currentIndex != targetIndex)
                items.Move(currentIndex, targetIndex);
        }

        while (items.Count > desired.Count)
            items.RemoveAt(items.Count - 1);
    }

    private async void TaskListGroupMenuItem_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MenuItemBase.IsExpanded) ||
            sender is not AccountTaskListGroupMenuItem item ||
            _taskMutationService is null ||
            item.Parameter.IsExpanded == item.IsExpanded)
            return;

        item.Parameter.IsExpanded = item.IsExpanded;
        await _taskMutationService.UpdateTaskListGroupAsync(item.Parameter).ConfigureAwait(false);
    }

    private void ApplyDesiredMenuItems(IReadOnlyList<IMenuItem> desired)
    {
        for (var targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var item = desired[targetIndex];
            var currentIndex = ShellMenu.Items.IndexOf(item);
            if (currentIndex < 0)
                ShellMenu.Items.Insert(targetIndex, item);
            else if (currentIndex != targetIndex)
                ShellMenu.Items.Move(currentIndex, targetIndex);
        }

        while (ShellMenu.Items.Count > desired.Count)
            ShellMenu.Items.RemoveAt(ShellMenu.Items.Count - 1);
    }

    private void ApplyMenuCounts()
    {
        var today = DateTime.UtcNow.Date;
        var open = _menuCountTasks.Values.Where(task => !task.IsCompleted).ToList();
        _myDayMenuItem.Count = open.Count(task => task.MyDayDateUtc == today);
        _plannedMenuItem.Count = open.Count(task => task.DueDate.HasValue);
        _importantMenuItem.Count = open.Count(task => task.IsImportant);

        foreach (var (listId, item) in _listMenuItems)
            item.Count = open.Count(task => task.TaskListId == listId);
    }

    private void UpdateSelectedMenuItemReference()
    {
        object selected = SelectedList is not null
            ? _listMenuItems.GetValueOrDefault(SelectedList.Id)
            : SelectedView switch
            {
                TaskViewKind.MyDay => _myDayMenuItem,
                TaskViewKind.Planned => _plannedMenuItem,
                TaskViewKind.Important => _importantMenuItem,
                _ => _myDayMenuItem
            };

        if (ReferenceEquals(_selectedMenuItem, selected))
            return;

        _selectedMenuItem = selected;
        OnPropertyChanged(nameof(IShellMenuProvider.SelectedMenuItem));
    }

    private int GetAccountOrder(Guid accountId)
        => Accounts.FirstOrDefault(account => account.Id == accountId)?.Order ?? int.MaxValue;

    private IReadOnlyList<AccountTask> MergePendingTasks(IReadOnlyList<AccountTask> stored)
        => stored
            .Where(task => !_pendingDeletedTaskIds.ContainsKey(task.Id) && !_pendingTaskStates.ContainsKey(task.Id))
            .Concat(_pendingTaskStates.Values)
            .ToList();

    private IReadOnlyList<AccountTaskList> MergePendingLists(IReadOnlyList<AccountTaskList> stored)
        => stored
            .Where(list => !_pendingDeletedListIds.ContainsKey(list.Id) && !_pendingListStates.ContainsKey(list.Id))
            .Concat(_pendingListStates.Values)
            .ToList();

    private string GetAccountName(Guid accountId)
        => Accounts.FirstOrDefault(account => account.Id == accountId)?.Name ?? Translator.ToDoPage_Accounts;

    private sealed record DesiredTaskGroup(
        string Key,
        Guid? AccountId,
        bool ShowHeader,
        IReadOnlyList<TaskItemViewModel> Items);

    private sealed record TaskReloadSnapshot(
        Guid? ListId,
        TaskViewKind View,
        TaskSortKind Sort,
        bool IsMyDaySelected,
        bool ShowListName,
        IReadOnlyList<AccountTask> PendingTasks,
        IReadOnlySet<Guid> PendingDeletedTaskIds);
}
