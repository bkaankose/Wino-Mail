using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Helpers;
using Wino.Mail.Controls.Core.ContextFlyout;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Views.Abstract;
using Wino.Core.Domain.Enums;
using WinRT;

namespace Wino.Views.ToDo;

public sealed partial class ToDoPage : ToDoPageAbstract, ITitleBarSearchHost
{
    private CancellationTokenSource? _searchCancellationTokenSource;
    private TaskItemViewModel? _moveTaskTarget;
    private TaskItemViewModel? _newStepTarget;
#if DEBUG
    private readonly INotificationBuilder _notificationBuilder =
        WinoApplication.Current.Services.GetRequiredService<INotificationBuilder>();
#endif

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsCompactLayout { get; set; }

    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
    public SearchBarMode SearchMode => SearchBarMode.Tasks;
    public string SearchText { get; set; } = string.Empty;

    private List<TaskItemViewModel> _draggedTasks = [];

    private CollectionViewSource TaskCollectionViewSource
    {
        [DynamicWindowsRuntimeCast(typeof(CollectionViewSource))]
        get => (CollectionViewSource)Resources["TaskCollectionViewSource"];
    }

    public ToDoPage()
    {
        InitializeComponent();

        TaskCollectionViewSource.Source = ViewModel.TaskGroups;

        // Native AOT needs the grouped view handed to the list in code. The generated
        // x:Bind path to CollectionViewSource.View does not root the grouped ABI.
        TaskListView.ItemsSource = TaskCollectionViewSource.View;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.TaskComposerFocusRequested -= ViewModel_TaskComposerFocusRequested;
        ViewModel.TaskComposerFocusRequested += ViewModel_TaskComposerFocusRequested;
        ViewModel.TaskSelectionRestored -= ViewModel_TaskSelectionRestored;
        ViewModel.TaskSelectionRestored += ViewModel_TaskSelectionRestored;
    }

    /// <summary>
    /// Keeps the list containers and the view model's selection in step after a reload. A cached page
    /// comes back with its containers still selected, and a rebuilt one comes back with none.
    /// </summary>
    private void ViewModel_TaskSelectionRestored(object? sender, IReadOnlyList<TaskItemViewModel> selection)
    {
        var current = TaskListView.SelectedItems.OfType<TaskItemViewModel>().ToList();
        if (current.Count == selection.Count && current.All(selection.Contains))
            return;

        TaskListView.SelectedItems.Clear();

        foreach (var item in selection)
            TaskListView.SelectedItems.Add(item);
    }

    partial void OnIsCompactLayoutPropertyChanged(DependencyPropertyChangedEventArgs e)
        => ViewModel.SetCompactLayout(IsCompactLayout);

    public async Task OnTitleBarSearchTextChangedAsync()
    {
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _searchCancellationTokenSource = null;
        SearchSuggestions.Clear();

        var queryText = SearchText;
        if (string.IsNullOrWhiteSpace(queryText))
            return;

        _searchCancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = _searchCancellationTokenSource.Token;
        try
        {
            await Task.Delay(150, cancellationToken);
            var tasks = await ViewModel.SearchTasksAsync(queryText, 6, cancellationToken);

            if (cancellationToken.IsCancellationRequested || !string.Equals(SearchText, queryText, StringComparison.Ordinal))
                return;

            foreach (var task in tasks)
            {
                SearchSuggestions.Add(new TitleBarSearchSuggestion(
                    task.Title,
                    ViewModel.GetTaskSearchSubtitle(task),
                    task));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion)
        => SearchText = suggestion?.Title ?? string.Empty;

    public async Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion)
    {
        SearchText = chosenSuggestion?.Title ?? queryText ?? string.Empty;

        var task = chosenSuggestion?.Tag as AccountTask
            ?? (await ViewModel.SearchTasksAsync(queryText, 1, CancellationToken.None)).FirstOrDefault();
        if (task is null)
            return;

        SearchSuggestions.Clear();
        var selectedItem = await ViewModel.LoadAndSelectTaskAsync(task.Id);
        if (selectedItem is not null)
        {
            TaskListView.SelectedItem = selectedItem;
            TaskListView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DetachFromViewModel();
        base.OnNavigatedFrom(e);
    }

    public override void PrepareForClose()
    {
        // A mode switch clears the frame content without OnNavigatedFrom.
        DetachFromViewModel();
        base.PrepareForClose();
    }

    /// <summary>
    /// The view model outlives the page, so everything that connects the two is taken down
    /// here: its events, the bindings, the grouped view the list is bound to, and the commands
    /// the buttons and menu items subscribed to.
    /// </summary>
    private void DetachFromViewModel()
    {
        ViewModel.TaskComposerFocusRequested -= ViewModel_TaskComposerFocusRequested;
        ViewModel.TaskSelectionRestored -= ViewModel_TaskSelectionRestored;
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _searchCancellationTokenSource = null;

        Bindings.StopTracking();
        TaskListView.SelectionChanged -= TaskListView_SelectionChanged;
        TaskListView.ItemsSource = null;
        TaskCollectionViewSource.Source = null;
        CommandSourceHelper.ReleaseCommands(this);
    }

    private void ViewModel_TaskComposerFocusRequested(object? sender, Wino.Core.Domain.Models.TaskComposerFocusRequestedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ComposerTextBox.Focus(FocusState.Programmatic));
    }

    private void TaskListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel.SetSelectedTasks(TaskListView.SelectedItems.OfType<TaskItemViewModel>());

    private void TaskListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedTasks = e.Items.OfType<TaskItemViewModel>().ToList();
        if (_draggedTasks.Count == 0 || !ViewModel.CanReorderTasks)
        {
            _draggedTasks = [];
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void TaskListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _draggedTasks = [];
        HideTaskDropIndicator();
    }

    private void TaskListView_DragLeave(object sender, DragEventArgs e) => HideTaskDropIndicator();

    private void TaskListView_DragOver(object sender, DragEventArgs e)
    {
        if (ResolveTaskDropTarget(e) is not { } drop)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            HideTaskDropIndicator();
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;
        TaskDropIndicatorTransform.Y = drop.IndicatorY;
        TaskDropIndicator.Visibility = Visibility.Visible;
    }

    private async void TaskListView_Drop(object sender, DragEventArgs e)
    {
        var drop = ResolveTaskDropTarget(e);
        var moved = _draggedTasks;
        _draggedTasks = [];
        HideTaskDropIndicator();

        if (drop is null || moved.Count == 0)
            return;

        await ViewModel.ReorderTasksAsync(moved, drop.Target, drop.InsertAfter);
    }

    private void HideTaskDropIndicator() => TaskDropIndicator.Visibility = Visibility.Collapsed;

    /// <summary>
    /// The list is bound to a grouped view, where the built-in ListView reorder mode is unavailable,
    /// so the drop position is resolved from the realized row containers.
    /// </summary>
    [DynamicWindowsRuntimeCast(typeof(ListViewItem))]
    private TaskDropTarget? ResolveTaskDropTarget(DragEventArgs e)
    {
        if (_draggedTasks.Count == 0 || !ViewModel.CanReorderTasks)
            return null;

        var pointerY = e.GetPosition(TaskListView).Y;
        TaskDropTarget? nearest = null;
        var nearestDistance = double.MaxValue;

        foreach (var item in ViewModel.TaskGroups.SelectMany(group => group))
        {
            if (TaskListView.ContainerFromItem(item) is not ListViewItem container || container.ActualHeight <= 0)
                continue;

            var top = container.TransformToVisual(TaskListView).TransformPoint(default).Y;
            var bottom = top + container.ActualHeight;
            var insertAfter = pointerY > top + (container.ActualHeight / 2);
            var distance = pointerY < top ? top - pointerY : pointerY > bottom ? pointerY - bottom : 0;
            if (distance >= nearestDistance)
                continue;

            nearestDistance = distance;
            nearest = new TaskDropTarget(item, insertAfter, insertAfter ? bottom + 1 : top - 3);
        }

        return nearest is null || _draggedTasks.Contains(nearest.Target) ? null : nearest;
    }

    private sealed record TaskDropTarget(TaskItemViewModel Target, bool InsertAfter, double IndicatorY);

    private async void TaskCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskItemViewModel item })
            await ViewModel.ToggleTaskCommand.ExecuteAsync(item);
    }

    private async void DetailCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not null)
            await ViewModel.ToggleTaskCommand.ExecuteAsync(ViewModel.SelectedTask);
    }

    private async void ImportanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskItemViewModel item })
            await ViewModel.ToggleImportanceCommand.ExecuteAsync(item);
    }

    private async void DetailImportanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not null)
            await ViewModel.ToggleImportanceCommand.ExecuteAsync(ViewModel.SelectedTask);
    }

    private void TaskContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: TaskItemViewModel task } target)
            return;

        var items = new List<ContextFlyoutMenuEntry>
        {
            CreateTaskCommand(task.MyDayActionText, WinoIconGlyph.CalendarToday, "ToDoTaskMyDay", ViewModel.ToggleMyDayCommand, task),
            CreateTaskCommand(task.ImportanceActionText, WinoIconGlyph.Star, "ToDoTaskImportance", ViewModel.ToggleImportanceCommand, task),
            CreateTaskCommand(task.CompletionActionText, WinoIconGlyph.MarkRead, "ToDoTaskCompletion", ViewModel.ToggleTaskCommand, task),
            ContextFlyoutSeparatorEntry.Instance,
            CreateTaskCommand(Translator.ToDoPage_DueToday, WinoIconGlyph.CalendarToday, "ToDoTaskDueToday", new AsyncRelayCommand(() => ViewModel.SetTaskDueDateAsync(task, DateTime.Now.Date)), isEnabled: task.IsEditable),
            CreateTaskCommand(Translator.ToDoPage_DueTomorrow, WinoIconGlyph.Calendar, "ToDoTaskDueTomorrow", new AsyncRelayCommand(() => ViewModel.SetTaskDueDateAsync(task, DateTime.Now.Date.AddDays(1))), isEnabled: task.IsEditable),
            CreateTaskCommand(Translator.ToDoPage_DuePresetPickDate, WinoIconGlyph.Calendar, "ToDoTaskPickDueDate", new AsyncRelayCommand(() => PickTaskDueDateAsync(task)), isEnabled: task.IsEditable),
            ContextFlyoutSeparatorEntry.Instance,
            CreateTaskCommand(Translator.ToDoPage_MoveTaskTo, WinoIconGlyph.Move, "ToDoTaskMove", new RelayCommand(() => ShowMoveTaskFlyout(task)), isEnabled: task.IsEditable),
            ContextFlyoutSeparatorEntry.Instance,
            CreateTaskCommand(
                Translator.ToDoPage_DeleteTask,
                WinoIconGlyph.Delete,
                "ToDoTaskDelete",
                ViewModel.DeleteTaskCommand,
                task,
                isDestructive: true,
                shortcut: new ContextFlyoutShortcut("Delete", "Delete"))
        };

#if DEBUG
        items.Add(ContextFlyoutSeparatorEntry.Instance);
        items.Add(CreateTaskCommand(
            Translator.Buttons_TestNotification,
            WinoIconGlyph.Reminder,
            "ToDoTaskTestNotification",
            new AsyncRelayCommand(() => _notificationBuilder.CreateTestTaskReminderNotificationAsync(task.Task))));
#endif

        WinoContextFlyoutHelper.Show(target, args, items);
    }

    private static ContextFlyoutCommandEntry CreateTaskCommand(
        string text,
        WinoIconGlyph icon,
        string automationId,
        System.Windows.Input.ICommand command,
        object? commandParameter = null,
        bool isEnabled = true,
        bool isDestructive = false,
        ContextFlyoutShortcut? shortcut = null)
        => new()
        {
            Text = text,
            Icon = CreateContextIcon(icon),
            Command = command,
            CommandParameter = commandParameter,
            IsEnabled = isEnabled && command.CanExecute(commandParameter),
            IsDestructive = isDestructive,
            Shortcut = shortcut,
            AutomationId = automationId
        };

    private static ContextFlyoutIcon? CreateContextIcon(WinoIconGlyph icon)
        => icon == WinoIconGlyph.None
            ? null
            : new ContextFlyoutIcon(WinoIconGlyphs.GetGlyph(icon));

    [DynamicWindowsRuntimeCast(typeof(DatePickerFlyout))]
    private async Task PickTaskDueDateAsync(TaskItemViewModel task)
    {
        if (TaskDueDatePickerHost.Flyout is not DatePickerFlyout picker)
            return;

        picker.Date = task.DueDate is { } dueDate
            ? new DateTimeOffset(dueDate)
            : new DateTimeOffset(DateTime.Now.Date);
        var selectedDate = await picker.ShowAtAsync(TaskListView);
        if (selectedDate is { } value)
            await ViewModel.SetTaskDueDateAsync(task, value.Date);
    }

    private void ShowMoveTaskFlyout(TaskItemViewModel task)
    {
        _moveTaskTarget = task;
        MoveTaskListView.ItemsSource = ViewModel.TaskLists
            .Where(list => !list.IsReadOnly &&
                           list.Id != task.Task.TaskListId &&
                           list.MailAccountId == task.Task.MailAccountId &&
                           list.SourceKind == task.Task.SourceKind)
            .ToList();
        var anchor = TaskListView.ContainerFromItem(task) as FrameworkElement ?? TaskListView;
        MoveTaskFlyoutHost.Flyout.ShowAt(anchor);
    }

    private async void MoveTaskListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (_moveTaskTarget is not { } item || e.ClickedItem is not AccountTaskList destination)
            return;

        MoveTaskFlyoutHost.Flyout.Hide();
        _moveTaskTarget = null;
        await ViewModel.MoveTaskAsync(item, destination);
    }

    private async void SuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskItemViewModel item })
            await ViewModel.AddSuggestionToMyDayCommand.ExecuteAsync(item);
    }

    private async void StepCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskStepViewModel step })
            await ViewModel.ToggleStepCommand.ExecuteAsync(step);
    }

    private async void StepTitleTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskStepViewModel step })
            await ViewModel.SaveStepCommand.ExecuteAsync(step);
    }

    private void AddStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not { IsEditable: true } task)
            return;

        _newStepTarget = task;
        NewStepTextBox.Text = string.Empty;
        AddStepButton.Visibility = Visibility.Collapsed;
        NewStepTextBox.Visibility = Visibility.Visible;
        NewStepTextBox.Focus(FocusState.Programmatic);
    }

    private async void NewStepTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            CloseNewStepEditor();
            AddStepButton.Focus(FocusState.Programmatic);
            return;
        }

        if (e.Key != Windows.System.VirtualKey.Enter)
            return;

        e.Handled = true;
        await CommitNewStepAsync();
    }

    private async void NewStepTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (NewStepTextBox.Visibility != Visibility.Visible)
            return;

        await CommitNewStepAsync();
        CloseNewStepEditor();
    }

    // The text is cleared before the await so a LostFocus raised while the step is queued cannot commit it twice.
    private Task CommitNewStepAsync()
    {
        var title = NewStepTextBox.Text;
        var target = _newStepTarget;
        NewStepTextBox.Text = string.Empty;

        return target is null || string.IsNullOrWhiteSpace(title)
            ? Task.CompletedTask
            : ViewModel.AddNamedStepAsync(target, title);
    }

    private void CloseNewStepEditor()
    {
        _newStepTarget = null;
        NewStepTextBox.Text = string.Empty;
        NewStepTextBox.Visibility = Visibility.Collapsed;
        AddStepButton.Visibility = Visibility.Visible;
    }

    private async void StepDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskStepViewModel step })
            await ViewModel.DeleteStepCommand.ExecuteAsync(step);
    }

    private async void DeleteTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not null)
            await ViewModel.DeleteTaskCommand.ExecuteAsync(ViewModel.SelectedTask);
    }

    private void ComposerTextBox_GotFocus(object sender, RoutedEventArgs e)
        => ViewModel.IsComposerExpanded = true;

    private void ComposerTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Keep the tools open while a preset is being picked; only collapse an empty composer.
        if (string.IsNullOrWhiteSpace(ViewModel.ComposerText))
            ViewModel.IsComposerExpanded = false;
    }

    private async void ComposerTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || string.IsNullOrWhiteSpace(ViewModel.ComposerText))
            return;

        e.Handled = true;
        await ViewModel.AddTaskCommand.ExecuteAsync(null);
    }

    /// <summary>Detail edits are committed when the field loses focus rather than on every keystroke.</summary>
    private async void DetailField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not null && ViewModel.CanEditSelectedTask)
            await ViewModel.SaveTaskCommand.ExecuteAsync(ViewModel.SelectedTask);
    }
}
