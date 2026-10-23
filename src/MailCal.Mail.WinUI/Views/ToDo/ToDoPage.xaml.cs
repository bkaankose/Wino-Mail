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
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Interfaces;
using MailCal.Helpers;
using MailCal.Mail.Controls.Core.ContextFlyout;
using MailCal.Mail.Controls.Core.SearchBar;
using MailCal.Mail.ViewModels.Data;
using MailCal.Mail.WinUI;
using MailCal.Mail.WinUI.Controls;
using MailCal.Mail.WinUI.Helpers;
using MailCal.Mail.WinUI.Interfaces;
using MailCal.Mail.WinUI.Models;
using MailCal.Views.Abstract;
using MailCal.Core.Domain.Enums;
using WinRT;

namespace MailCal.Views.ToDo;

public sealed partial class ToDoPage : ToDoPageAbstract, ITitleBarSearchHost
{
    private CancellationTokenSource? _searchCancellationTokenSource;
    private TaskItemViewModel? _moveTaskTarget;
#if DEBUG
    private readonly INotificationBuilder _notificationBuilder =
        MailCalApplication.Current.Services.GetRequiredService<INotificationBuilder>();
#endif

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsCompactLayout { get; set; }

    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
    public SearchBarMode SearchMode => SearchBarMode.Tasks;
    public string SearchText { get; set; } = string.Empty;

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

    private void ViewModel_TaskComposerFocusRequested(object? sender, MailCal.Core.Domain.Models.TaskComposerFocusRequestedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => ComposerTextBox.Focus(FocusState.Programmatic));
    }

    private void TaskListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ViewModel.SetSelectedTasks(TaskListView.SelectedItems.OfType<TaskItemViewModel>());

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
            CreateTaskCommand(task.MyDayActionText, MailCalIconGlyph.CalendarToday, "ToDoTaskMyDay", ViewModel.ToggleMyDayCommand, task),
            CreateTaskCommand(task.ImportanceActionText, MailCalIconGlyph.Star, "ToDoTaskImportance", ViewModel.ToggleImportanceCommand, task),
            CreateTaskCommand(task.CompletionActionText, MailCalIconGlyph.MarkRead, "ToDoTaskCompletion", ViewModel.ToggleTaskCommand, task),
            ContextFlyoutSeparatorEntry.Instance,
            CreateTaskCommand(Translator.ToDoPage_DueToday, MailCalIconGlyph.CalendarToday, "ToDoTaskDueToday", new AsyncRelayCommand(() => ViewModel.SetTaskDueDateAsync(task, DateTime.Now.Date)), isEnabled: task.IsEditable),
            CreateTaskCommand(Translator.ToDoPage_DueTomorrow, MailCalIconGlyph.Calendar, "ToDoTaskDueTomorrow", new AsyncRelayCommand(() => ViewModel.SetTaskDueDateAsync(task, DateTime.Now.Date.AddDays(1))), isEnabled: task.IsEditable),
            CreateTaskCommand(Translator.ToDoPage_DuePresetPickDate, MailCalIconGlyph.Calendar, "ToDoTaskPickDueDate", new AsyncRelayCommand(() => PickTaskDueDateAsync(task)), isEnabled: task.IsEditable),
            ContextFlyoutSeparatorEntry.Instance,
            CreateTaskCommand(Translator.ToDoPage_MoveTaskTo, MailCalIconGlyph.Move, "ToDoTaskMove", new RelayCommand(() => ShowMoveTaskFlyout(task)), isEnabled: task.IsEditable),
            ContextFlyoutSeparatorEntry.Instance,
            CreateTaskCommand(
                Translator.ToDoPage_DeleteTask,
                MailCalIconGlyph.Delete,
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
            MailCalIconGlyph.Reminder,
            "ToDoTaskTestNotification",
            new AsyncRelayCommand(() => _notificationBuilder.CreateTestTaskReminderNotificationAsync(task.Task))));
#endif

        MailCalContextFlyoutHelper.Show(target, args, items);
    }

    private static ContextFlyoutCommandEntry CreateTaskCommand(
        string text,
        MailCalIconGlyph icon,
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

    private static ContextFlyoutIcon? CreateContextIcon(MailCalIconGlyph icon)
        => icon == MailCalIconGlyph.None
            ? null
            : new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(icon));

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
        MoveTaskFlyoutHost.Flyout.ShowAt(TaskListView);
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
