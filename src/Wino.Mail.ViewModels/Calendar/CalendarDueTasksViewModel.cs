using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.AppLock;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Helpers;
using Wino.Services.Interfaces;

namespace Wino.Mail.ViewModels.Calendar;

/// <summary>
/// The open tasks due in the calendar's visible days, shown beside the events. Composition only: the tasks
/// are read from the task store, and ticking one off completes it through the task request pipeline.
/// </summary>
public sealed partial class CalendarDueTasksViewModel : ObservableObject
{
    private readonly ITaskQueryService _taskQueryService;
    private readonly IWinoRequestDelegator _requestDelegator;
    private readonly IPreferencesService _preferencesService;
    private readonly IMailDialogService _dialogService;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Func<Action, Task> _runOnUiThread = action => { action(); return Task.CompletedTask; };
    private VisibleDateRange _range;

    public CalendarDueTasksViewModel(
        IMessenger messenger,
        ITaskQueryService taskQueryService,
        IWinoRequestDelegator requestDelegator,
        IPreferencesService preferencesService,
        IMailDialogService dialogService)
    {
        _taskQueryService = taskQueryService;
        _requestDelegator = requestDelegator;
        _preferencesService = preferencesService;
        _dialogService = dialogService;

        messenger.Register<CalendarDueTasksViewModel, TaskStateChanged>(this, static (recipient, message) => _ = recipient.RefreshAsync());
        messenger.Register<CalendarDueTasksViewModel, BulkTaskChanged>(this, static (recipient, message) => _ = recipient.RefreshAsync());
        messenger.Register<CalendarDueTasksViewModel, PreferenceChangedMessage>(this, static (recipient, message) =>
        {
            if (message.PropertyName == nameof(IPreferencesService.ShowTasksInCalendar))
                _ = recipient.RefreshAsync();
        });
    }

    public ObservableCollection<TaskItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    /// <summary>The calendar page hands over its UI-thread runner.</summary>
    public void Initialize(Func<Action, Task> runOnUiThread)
        => _runOnUiThread = runOnUiThread ?? _runOnUiThread;

    public Task SetRangeAsync(VisibleDateRange range)
    {
        _range = range;
        return RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var range = _range;
        await _refreshGate.WaitAsync().ConfigureAwait(false);

        try
        {
            var due = range is null || !_preferencesService.ShowTasksInCalendar
                ? []
                : await LoadAsync(range).ConfigureAwait(false);

            await _runOnUiThread(() =>
            {
                Items.Clear();
                foreach (var item in due)
                    Items.Add(item);
                HasItems = Items.Count > 0;
            }).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<TaskItemViewModel[]> LoadAsync(VisibleDateRange range)
    {
        var start = range.StartDate.ToDateTime(TimeOnly.MinValue);
        var end = range.EndDate.ToDateTime(TimeOnly.MinValue);
        var tasks = await _taskQueryService.GetTasksAsync(view: TaskViewKind.Planned, sort: TaskSortKind.DueDate).ConfigureAwait(false) ?? [];
        var lists = await _taskQueryService.GetTaskListsAsync().ConfigureAwait(false) ?? [];
        var names = lists.ToDictionary(list => list.Id, list => list.Title ?? string.Empty);

        return tasks
            .Where(task => !task.IsCompleted && task.DueDate is { } dueDate && dueDate.Date >= start && dueDate.Date <= end)
            .OrderBy(task => task.DueDate)
            .ThenByDescending(task => task.IsImportant)
            .Select(task => new TaskItemViewModel(task, names.TryGetValue(task.TaskListId, out var name) ? name : string.Empty) { ShowListName = true })
            .ToArray();
    }

    [RelayCommand]
    private async Task CompleteAsync(TaskItemViewModel item)
    {
        if (item is null || item.IsReadOnly || item.IsCompleted)
            return;

        var plan = await TaskCompletionPlanner.BuildAsync(item.Task, isCompleted: true, _taskQueryService).ConfigureAwait(false);

        try
        {
            await _requestDelegator.ExecuteAsync(item.Task.MailAccountId, TaskCompletionPlanner.Requests(plan)).ConfigureAwait(false);
        }
        catch (AppLockedException ex)
        {
            _dialogService.InfoBarMessage(Wino.Core.Domain.Translator.AppLock_LockedTitle, ex.Message, InfoBarMessageType.Warning);
        }
    }
}
