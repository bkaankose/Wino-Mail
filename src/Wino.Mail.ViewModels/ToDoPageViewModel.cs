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
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// To Do mode state and shell menu. Provider data is kept in ITaskService; this VM only
/// projects the selected list/view and performs optimistic local mutations.
/// </summary>
public partial class ToDoPageViewModel : MailBaseViewModel, IShellMenuOwner, IShellMenuProvider, IRecipient<TaskSynchronizationCompleted>
{
    private readonly ITaskService _taskService;
    private readonly IAccountService _accountService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly INavigationService _navigationService;
    private readonly ICalendarService _calendarService;
    private readonly IMailDialogService _dialogService;
    private readonly NewTaskListMenuItem _newListMenuItem = new();
    private readonly List<TaskSmartViewMenuItem> _smartViewItems = [];
    private readonly List<AccountTaskListMenuItem> _listMenuItems = [];
    private readonly SemaphoreSlim _reloadSemaphore = new(1, 1);
    private bool _isPaneCompact;
    private bool _isPreparedForShellShutdown;
    private object _selectedMenuItem;
    private long _requestedFullReloadVersion;
    private long _completedFullReloadVersion;
    private long _taskReloadVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value)
        => _ = ReloadTasksAsync();

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

    [ObservableProperty]
    public partial bool IsCompletedGroupExpanded { get; set; } = true;

    partial void OnSelectedSortChanged(TaskSortKind value)
        => _ = ReloadTasksAsync();

    partial void OnIsCompletedGroupExpandedChanged(bool value)
        => _ = ReloadTasksAsync();

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
            SelectedView = TaskViewKind.All;
        OnPropertyChanged(nameof(CanCreateTask));
        OnPropertyChanged(nameof(IsQuickAddVisible));
        OnPropertyChanged(nameof(IsNamedListSelected));
        OnPropertyChanged(nameof(IsSmartViewSelected));
        OnPropertyChanged(nameof(IsMyDaySelected));
        OnPropertyChanged(nameof(CanEditSelectedList));
        OnPropertyChanged(nameof(IsSelectedListReadOnly));
        OnPropertyChanged(nameof(CanEditSelectedTask));
        OnPropertyChanged(nameof(SelectedSurfaceTitle));
        OnPropertyChanged(nameof(SelectedSurfaceSubtitle));
        OnPropertyChanged(nameof(HasSurfaceSubtitle));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        UpdateSelectedMenuItemReference();
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
    }

    public ObservableCollection<AccountTaskList> TaskLists { get; } = [];
    public ObservableCollection<TaskGroup> TaskGroups { get; } = [];
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

    /// <summary>Only My Day carries a subtitle, and it is today's date.</summary>
    public string SelectedSurfaceSubtitle
        => IsMyDaySelected ? DateTime.Now.ToString("dddd, MMMM d") : string.Empty;

    public bool HasSurfaceSubtitle => IsMyDaySelected;

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
                    SelectedView = smartView.Parameter;
                    SelectedList = null;
                    _ = ReloadTasksAsync();
                    break;
                case AccountTaskListMenuItem list:
                    SelectedView = TaskViewKind.All;
                    SelectedList = list.Parameter;
                    _ = ReloadTasksAsync();
                    break;
            }
        }
    }

    public ToDoPageViewModel(
        ITaskService taskService,
        IAccountService accountService,
        IWinoRequestDelegator requestDelegator,
        INavigationService navigationService,
        ICalendarService calendarService,
        IMailDialogService dialogService)
    {
        _taskService = taskService;
        _accountService = accountService;
        _requestDelegator = requestDelegator;
        _navigationService = navigationService;
        _calendarService = calendarService;
        _dialogService = dialogService;
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
        RebuildShellMenu();
        OnPropertyChanged(nameof(IShellMenuProvider.ShellMenu));
    }

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();
        Messenger.Register<TaskSynchronizationCompleted>(this);
    }

    protected override void UnregisterRecipients()
    {
        Messenger.Unregister<TaskSynchronizationCompleted>(this);
        base.UnregisterRecipients();
    }

    public void Receive(TaskSynchronizationCompleted message)
        => _ = HandleTaskSynchronizationCompletedAsync(message);

    private async Task HandleTaskSynchronizationCompletedAsync(TaskSynchronizationCompleted message)
    {
        var isKnownAccount = false;
        await ExecuteUIThread(() => isKnownAccount = Accounts.Any(account => account.Id == message.AccountId)).ConfigureAwait(false);
        if (isKnownAccount)
            await ReloadAsync().ConfigureAwait(false);
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
        _ = ExecuteUIThread(RebuildShellMenu);
    }

    public void SetCompactLayout(bool isCompact)
    {
        if (IsCompactLayout == isCompact)
            return;

        IsCompactLayout = isCompact;
        if (isCompact && SelectedTask is null)
            OnPropertyChanged(nameof(IsTaskListSurfaceVisible));
    }

    public Task OnMenuSelectionChangedAsync(IMenuItem menuItem)
    {
        if (menuItem is TaskSmartViewMenuItem smartView)
        {
            SelectedView = smartView.Parameter;
            SelectedList = null;
        }
        else if (menuItem is AccountTaskListMenuItem list)
            SelectedList = list.Parameter;
        _ = ReloadTasksAsync();
        return Task.CompletedTask;
    }

    public Task OnMenuItemInvokedAsync(IMenuItem menuItem)
    {
        switch (menuItem)
        {
            case NewTaskListMenuItem:
                return CreateListAsync();
            case TaskSmartViewMenuItem smartView:
                SelectedView = smartView.Parameter;
                SelectedList = null;
                return ReloadTasksAsync();
            case AccountTaskListMenuItem list:
                SelectedList = list.Parameter;
                SelectedView = TaskViewKind.All;
                return ReloadTasksAsync();
            default:
                return Task.CompletedTask;
        }
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
        TaskGroups.Clear();
        Suggestions.Clear();
        Accounts.Clear();
        SelectedTask = null;
        SelectedList = null;
        _selectedMenuItem = null;
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
            foreach (var account in accounts.Where(RequiresLocalFallbackList))
                await _taskService.GetOrCreateLocalTaskListAsync(account.Id, account.Name).ConfigureAwait(false);

            var lists = await _taskService.GetTaskListsAsync().ConfigureAwait(false);
            await ExecuteUIThread(() =>
            {
                var selectedListId = SelectedList?.Id;
                Accounts.Clear();
                foreach (var account in accounts)
                    Accounts.Add(account);

                TaskLists.Clear();
                foreach (var list in lists
                             .OrderBy(list => list.MailAccountId)
                             .ThenByDescending(list => list.IsDefault)
                             .ThenBy(list => list.Title, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(list => list.Id))
                {
                    TaskLists.Add(list);
                }

                SelectedList = selectedListId is { } id
                    ? TaskLists.FirstOrDefault(list => list.Id == id)
                    : null;
                OnPropertyChanged(nameof(CanCreateTask));
                OnPropertyChanged(nameof(IsQuickAddVisible));
                OnPropertyChanged(nameof(CanEditSelectedTask));
                AddTaskCommand.NotifyCanExecuteChanged();
                RebuildShellMenu();
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
            SearchText,
            SelectedSort,
            IsMyDaySelected,
            SelectedList is null)).ConfigureAwait(false);

        var tasks = await _taskService.GetTasksAsync(
            listId: snapshot.ListId,
            view: snapshot.View,
            search: snapshot.SearchText,
            sort: snapshot.Sort).ConfigureAwait(false);

        var suggestions = snapshot.IsMyDaySelected
            ? await _taskService.GetMyDaySuggestionsAsync().ConfigureAwait(false)
            : [];

        await ExecuteUIThread(() =>
        {
            if (reloadVersion != Volatile.Read(ref _taskReloadVersion))
                return;

            var previousSelectionId = SelectedTask?.Id;

            var wrapped = tasks.Select(task => new TaskItemViewModel(task, ResolveListName(task))
            {
                ShowListName = snapshot.ShowListName
            }).ToList();

            TaskGroups.Clear();

            var active = wrapped.Where(item => !item.IsCompleted).ToList();
            var completed = wrapped.Where(item => item.IsCompleted).ToList();

            if (active.Count > 0)
            {
                var activeGroup = new TaskGroup(string.Empty, isCompletedGroup: false);
                foreach (var item in active)
                    activeGroup.Add(item);
                TaskGroups.Add(activeGroup);
            }

            // The completed group always publishes its header; collapsing empties the group
            // rather than removing it, so the header stays reachable.
            if (completed.Count > 0)
            {
                var completedGroup = new TaskGroup(Translator.ToDoPage_CompletedGroupHeader, isCompletedGroup: true);
                if (IsCompletedGroupExpanded)
                {
                    foreach (var item in completed)
                        completedGroup.Add(item);
                }
                TaskGroups.Add(completedGroup);
            }

            Suggestions.Clear();
            foreach (var suggestion in suggestions)
                Suggestions.Add(new TaskItemViewModel(suggestion, ResolveListName(suggestion)) { ShowListName = true });

            SelectedTask = previousSelectionId is { } id
                ? wrapped.FirstOrDefault(item => item.Id == id)
                : null;

            OnPropertyChanged(nameof(CanCreateTask));
            OnPropertyChanged(nameof(CanEditSelectedTask));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(EmptyStateTitle));
            OnPropertyChanged(nameof(EmptyStateBody));
            UpdateMenuCounts();
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
        var task = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = destination.MailAccountId,
            TaskListId = destination.Id,
            SourceKind = destination.SourceKind,
            Title = title,
            DueDate = ComposerDueDate ?? (SelectedList is null && SelectedView == TaskViewKind.Planned ? DateTime.Now.Date : null),
            IsImportant = SelectedList is null && SelectedView == TaskViewKind.Important,
            MyDayDateUtc = IsMyDaySelected ? DateTime.UtcNow.Date : null
        }).ConfigureAwait(false);

        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(task.MailAccountId, TaskSynchronizerOperation.CreateTask, Task: task));

        await ExecuteUIThread(() =>
        {
            ComposerText = string.Empty;
            ComposerDueDate = null;
        }).ConfigureAwait(false);

        await ReloadTasksAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly)
            return;

        // Flip locally first so the row repaints immediately, then persist.
        item.IsCompleted = !item.IsCompleted;
        await _taskService.UpdateTaskAsync(item.Task).ConfigureAwait(false);
        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(item.Task.MailAccountId, TaskSynchronizerOperation.UpdateTask, Task: item.Task));
        await ReloadTasksAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ToggleImportanceAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly)
            return;

        item.IsImportant = !item.IsImportant;
        await _taskService.UpdateTaskAsync(item.Task).ConfigureAwait(false);
        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(item.Task.MailAccountId, TaskSynchronizerOperation.UpdateTask, Task: item.Task));
        await ExecuteUIThread(UpdateMenuCounts).ConfigureAwait(false);

        // The Important surface is filtered on the flag being toggled, so it has to requery.
        if (SelectedList is null && SelectedView == TaskViewKind.Important)
            await ReloadTasksAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private Task AddToMyDayAsync(TaskItemViewModel item)
        => SetMyDayAsync(item, DateTime.UtcNow.Date);

    [RelayCommand]
    private Task RemoveFromMyDayAsync(TaskItemViewModel item)
        => SetMyDayAsync(item, null);

    private async Task SetMyDayAsync(TaskItemViewModel item, DateTime? value)
    {
        if (item is null || item.IsReadOnly)
            return;

        item.MyDayDateUtc = value;
        await _taskService.UpdateTaskAsync(item.Task).ConfigureAwait(false);
        await ReloadTasksAsync().ConfigureAwait(false);
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

        item.MyDayDateUtc = DateTime.UtcNow.Date;
        await _taskService.UpdateTaskAsync(item.Task).ConfigureAwait(false);
        await ReloadTasksAsync().ConfigureAwait(false);
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

        SelectedTask.DueDate = due;
        SelectedTaskDueDate = due is { } value ? new DateTimeOffset(value) : null;
        await SaveTaskAsync(SelectedTask).ConfigureAwait(false);
        await ReloadTasksAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task SaveTaskAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly)
            return;

        var steps = item.Steps.Select(step => step.Step).ToList();
        await _taskService.UpdateTaskAsync(item.Task).ConfigureAwait(false);
        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(item.Task.MailAccountId, TaskSynchronizerOperation.UpdateTask, Task: item.Task));

        foreach (var step in steps)
        {
            await _taskService.UpdateStepAsync(step).ConfigureAwait(false);
            await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(step.MailAccountId, TaskSynchronizerOperation.UpdateStep, Task: item.Task, Step: step));
        }
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly)
            return;

        await _taskService.DeleteTaskAsync(item.Id).ConfigureAwait(false);
        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(item.Task.MailAccountId, TaskSynchronizerOperation.DeleteTask, Task: item.Task));
        await ExecuteUIThread(() => SelectedTask = null).ConfigureAwait(false);
        await ReloadTasksAsync().ConfigureAwait(false);
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
        var step = await _taskService.CreateStepAsync(new AccountTaskStep
        {
            TaskId = selectedTask.Id,
            MailAccountId = selectedTask.Task.MailAccountId,
            SourceKind = selectedTask.Task.SourceKind,
            Title = Translator.ToDoPage_NewStep,
            Order = selectedTask.Steps.Count
        }).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            if (SelectedTask?.Id != selectedTask.Id)
                return;

            selectedTask.Task.Steps.Add(step);
            selectedTask.Steps.Add(new TaskStepViewModel(step));
        }).ConfigureAwait(false);

        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(step.MailAccountId, TaskSynchronizerOperation.CreateStep, Task: selectedTask.Task, Step: step));
    }

    [RelayCommand]
    private async Task ToggleStepAsync(TaskStepViewModel step)
    {
        if (step is null || !CanEditSelectedTask)
            return;

        var selectedTask = SelectedTask;
        step.IsCompleted = !step.IsCompleted;
        selectedTask?.RefreshStepSummary();
        await _taskService.UpdateStepAsync(step.Step).ConfigureAwait(false);
        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(step.Step.MailAccountId, TaskSynchronizerOperation.UpdateStep, Task: selectedTask?.Task, Step: step.Step));
    }

    [RelayCommand]
    private async Task DeleteStepAsync(TaskStepViewModel step)
    {
        if (step is null || !CanEditSelectedTask)
            return;

        var selectedTask = SelectedTask;
        await _taskService.DeleteStepAsync(step.Step.Id).ConfigureAwait(false);
        await ExecuteUIThread(() =>
        {
            if (SelectedTask?.Id != selectedTask?.Id)
                return;

            selectedTask.Steps.Remove(step);
            selectedTask.Task.Steps.Remove(step.Step);
        }).ConfigureAwait(false);

        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(step.Step.MailAccountId, TaskSynchronizerOperation.DeleteStep, Task: selectedTask?.Task, Step: step.Step));
    }

    [RelayCommand]
    private async Task CreateListAsync()
    {
        var account = Accounts.FirstOrDefault(account => account.IsTaskAccessEnabled);
        if (account is null)
            return;
        var list = await _taskService.CreateTaskListAsync(account.Id, Translator.ToDoPage_NewList).ConfigureAwait(false);
        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(account.Id, TaskSynchronizerOperation.CreateList, List: list));
        await ReloadAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task DeleteListAsync()
    {
        if (SelectedList is null || SelectedList.IsReadOnly)
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

        await _taskService.DeleteTaskListAsync(list.Id).ConfigureAwait(false);
        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(list.MailAccountId, TaskSynchronizerOperation.DeleteList, List: list));
        await ExecuteUIThread(() =>
        {
            SelectedList = null;
            SelectedView = TaskViewKind.MyDay;
        }).ConfigureAwait(false);
        await ReloadAsync().ConfigureAwait(false);
    }

    /// <summary>Commits an inline header rename. Called on Enter or when the header loses focus.</summary>
    [RelayCommand]
    private async Task RenameListAsync(string title)
    {
        if (SelectedList is null || SelectedList.IsReadOnly)
            return;

        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == SelectedList.Title)
            return;

        var selectedList = SelectedList;
        selectedList.Title = trimmed;
        await _taskService.UpdateTaskListAsync(selectedList).ConfigureAwait(false);
        await QueueMutationAsync(new Wino.Core.Requests.Tasks.TaskActionRequest(
            selectedList.MailAccountId,
            TaskSynchronizerOperation.UpdateList,
            List: selectedList));

        await ExecuteUIThread(() =>
        {
            OnPropertyChanged(nameof(SelectedSurfaceTitle));
            RebuildShellMenu();
        }).ConfigureAwait(false);
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

    private async Task QueueMutationAsync(Wino.Core.Requests.Tasks.TaskActionRequest request)
        => await _requestDelegator.ExecuteAsync(request.MailAccountId, [request]).ConfigureAwait(false);

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

    private void RebuildShellMenu()
    {
        if (ShellMenu?.Items is null)
            return;
        _smartViewItems.Clear();
        _listMenuItems.Clear();
        var desired = new List<IMenuItem>();
        _smartViewItems.Add(new TaskSmartViewMenuItem(TaskViewKind.MyDay, Translator.ToDoPage_MyDay, "\uE706"));
        _smartViewItems.Add(new TaskSmartViewMenuItem(TaskViewKind.Important, Translator.ToDoPage_Important, "\uE734"));
        _smartViewItems.Add(new TaskSmartViewMenuItem(TaskViewKind.Planned, Translator.ToDoPage_Planned, "\uE787"));
        _smartViewItems.Add(new TaskSmartViewMenuItem(TaskViewKind.All, Translator.ToDoPage_Tasks, "\uE8FD"));
        _smartViewItems.Add(new TaskSmartViewMenuItem(TaskViewKind.Completed, Translator.ToDoPage_Completed, "\uE73E"));
        desired.AddRange(_smartViewItems);
        foreach (var accountGroup in TaskLists.GroupBy(list => list.MailAccountId))
        {
            var account = Accounts.FirstOrDefault(item => item.Id == accountGroup.Key);
            if (!_isPaneCompact)
                desired.Add(new ShellSectionHeaderMenuItem(account?.Name ?? Translator.ToDoPage_Accounts));
            foreach (var list in accountGroup)
            {
                var item = new AccountTaskListMenuItem(list, account?.Name);
                _listMenuItems.Add(item);
                desired.Add(item);
            }
        }
        while (ShellMenu.Items.Count > 0)
            ShellMenu.Items.RemoveAt(ShellMenu.Items.Count - 1);
        foreach (var item in desired)
            ShellMenu.Items.Add(item);

        ShellMenu.FooterItems?.Clear();
        ShellMenu.FooterItems?.Add(_newListMenuItem);

        UpdateSelectedMenuItemReference();
        _ = RefreshMenuCountsAsync();
    }

    /// <summary>
    /// Recomputes pane badges from the full task cache. The visible surface only ever holds one
    /// view's worth of tasks, so the counts cannot be derived from <see cref="TaskGroups"/>.
    /// </summary>
    private async Task RefreshMenuCountsAsync()
    {
        var all = await _taskService.GetTasksAsync().ConfigureAwait(false);
        await ExecuteUIThread(() => ApplyMenuCounts(all)).ConfigureAwait(false);
    }

    private void UpdateMenuCounts()
        => _ = RefreshMenuCountsAsync();

    private void ApplyMenuCounts(IReadOnlyList<AccountTask> all)
    {
        var today = DateTime.UtcNow.Date;
        var open = all.Where(task => !task.IsCompleted).ToList();

        foreach (var item in _smartViewItems)
        {
            item.Count = item.Parameter switch
            {
                TaskViewKind.MyDay => open.Count(task => task.MyDayDateUtc == today),
                TaskViewKind.Important => open.Count(task => task.IsImportant),
                TaskViewKind.Planned => open.Count(task => task.DueDate.HasValue),
                TaskViewKind.Completed => 0,
                _ => open.Count
            };
        }

        foreach (var item in _listMenuItems)
            item.Count = open.Count(task => task.TaskListId == item.Parameter?.Id);
    }

    private void UpdateSelectedMenuItemReference()
    {
        object selected = SelectedList is not null
            ? _listMenuItems.FirstOrDefault(item => item.Parameter?.Id == SelectedList.Id)
            : _smartViewItems.FirstOrDefault(item => item.Parameter == SelectedView);

        if (ReferenceEquals(_selectedMenuItem, selected))
            return;

        _selectedMenuItem = selected;
        OnPropertyChanged(nameof(IShellMenuProvider.SelectedMenuItem));
    }

    private sealed record TaskReloadSnapshot(
        Guid? ListId,
        TaskViewKind View,
        string SearchText,
        TaskSortKind Sort,
        bool IsMyDaySelected,
        bool ShowListName);
}
