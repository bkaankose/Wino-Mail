using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Views.Abstract;

namespace Wino.Views.ToDo;

public sealed partial class ToDoPage : ToDoPageAbstract, ITitleBarSearchHost
{
    private CancellationTokenSource? _searchCancellationTokenSource;

    [GeneratedDependencyProperty(DefaultValue = false)]
    public partial bool IsCompactLayout { get; set; }

    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
    public SearchBarMode SearchMode => SearchBarMode.Tasks;
    public string SearchText { get; set; } = string.Empty;

    public string SearchPlaceholderText => Translator.ToDoPage_Search;

    private CollectionViewSource TaskCollectionViewSource => (CollectionViewSource)Resources["TaskCollectionViewSource"];

    public ToDoPage()
    {
        InitializeComponent();

        TaskCollectionViewSource.Source = ViewModel.TaskGroups;

        // Native AOT needs the grouped view handed to the list in code. The generated
        // x:Bind path to CollectionViewSource.View does not root the grouped ABI.
        TaskListView.ItemsSource = TaskCollectionViewSource.View;
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
            TaskListView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _searchCancellationTokenSource = null;
        base.OnNavigatedFrom(e);
    }

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

    private async void MyDayButton_Click(object sender, RoutedEventArgs e)
    {
        var item = ViewModel.SelectedTask;
        if (item is null)
            return;

        if (item.IsInMyDay)
            await ViewModel.RemoveFromMyDayCommand.ExecuteAsync(item);
        else
            await ViewModel.AddToMyDayCommand.ExecuteAsync(item);
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

    /// <summary>Puts the caret in the header so the rename entry has somewhere to go.</summary>
    private void RenameListMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ListTitleTextBox.Focus(FocusState.Programmatic);
        ListTitleTextBox.SelectAll();
    }

    private async void ListTitleTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;

        e.Handled = true;
        await ViewModel.RenameListCommand.ExecuteAsync(ListTitleTextBox.Text);
    }

    private async void ListTitleTextBox_LostFocus(object sender, RoutedEventArgs e)
        => await ViewModel.RenameListCommand.ExecuteAsync(ListTitleTextBox.Text);

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

    private void ComposerDueToday_Click(object sender, RoutedEventArgs e)
        => ViewModel.ComposerDueDate = DateTime.Now.Date;

    private void ComposerDueTomorrow_Click(object sender, RoutedEventArgs e)
        => ViewModel.ComposerDueDate = DateTime.Now.Date.AddDays(1);

    /// <summary>Detail edits are committed when the field loses focus rather than on every keystroke.</summary>
    private async void DetailField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTask is not null && ViewModel.CanEditSelectedTask)
            await ViewModel.SaveTaskCommand.ExecuteAsync(ViewModel.SelectedTask);
    }
}
