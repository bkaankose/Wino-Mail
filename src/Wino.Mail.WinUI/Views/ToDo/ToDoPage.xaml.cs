using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Wino.Core.Domain;
using Wino.Mail.Controls.Core.SearchBar;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Interfaces;
using Wino.Mail.WinUI.Models;
using Wino.Views.Abstract;

namespace Wino.Views.ToDo;

public sealed partial class ToDoPage : ToDoPageAbstract, ITitleBarSearchHost
{
    public ObservableCollection<TitleBarSearchSuggestion> SearchSuggestions { get; } = [];
    public SearchBarMode SearchMode => SearchBarMode.Tasks;
    public string SearchText
    {
        get => ViewModel.SearchText;
        set => ViewModel.SearchText = value ?? string.Empty;
    }

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

    public Task OnTitleBarSearchTextChangedAsync() => Task.CompletedTask;

    public void OnTitleBarSearchSuggestionChosen(TitleBarSearchSuggestion suggestion)
        => SearchText = suggestion?.Title ?? string.Empty;

    public Task OnTitleBarSearchSubmittedAsync(string queryText, TitleBarSearchSuggestion? chosenSuggestion)
    {
        SearchText = chosenSuggestion?.Title ?? queryText ?? string.Empty;
        return Task.CompletedTask;
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

    private void CompletedGroupHeader_Click(object sender, RoutedEventArgs e)
        => ViewModel.IsCompletedGroupExpanded = !ViewModel.IsCompletedGroupExpanded;

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

    private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        => ViewModel.SetCompactLayout(e.NewSize.Width < 1008);
}
