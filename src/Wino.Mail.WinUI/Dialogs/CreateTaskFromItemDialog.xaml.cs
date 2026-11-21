using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Tasks;

namespace Wino.Mail.WinUI.Dialogs;

public sealed record TaskDueOption(string Label, DateTime? Date);

public sealed partial class CreateTaskFromItemDialog : ContentDialog, INotifyPropertyChanged
{
    private string _taskTitle;
    private TaskListPickerItem? _selectedList;

    internal TaskListPickerItem[] Lists { get; }
    internal TaskDueOption[] DueOptions { get; }
    internal TaskDueOption SelectedDue { get; set; }
    internal string TaskNotes { get; set; }

    internal string TaskTitle
    {
        get => _taskTitle;
        set
        {
            if (_taskTitle == value) return;
            _taskTitle = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCreate)));
        }
    }

    internal TaskListPickerItem? SelectedList
    {
        get => _selectedList;
        set
        {
            if (ReferenceEquals(_selectedList, value)) return;
            _selectedList = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanCreate)));
        }
    }

    internal bool CanCreate => SelectedList is not null && !string.IsNullOrWhiteSpace(TaskTitle);

    public TaskFromItemResult? Result { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CreateTaskFromItemDialog(TaskFromItemDraft draft)
    {
        _taskTitle = draft.Title;
        TaskNotes = draft.Notes;
        Lists = draft.Lists
            .Select(list => new TaskListPickerItem(list, draft.Accounts.FirstOrDefault(account => account.Id == list.MailAccountId)))
            .ToArray();
        _selectedList = Lists.FirstOrDefault(item => item.List.Id == draft.SelectedListId) ?? Lists.FirstOrDefault();
        DueOptions = CreateDueOptions(draft.DueDate);
        SelectedDue = DueOptions.FirstOrDefault(option => option.Date == draft.DueDate?.Date) ?? DueOptions[0];

        InitializeComponent();
    }

    private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => TitleBox.SelectAll();

    private static TaskDueOption[] CreateDueOptions(DateTime? suggested)
    {
        var today = DateTime.Today;
        var options = new List<TaskDueOption>
        {
            new(Translator.ConnectedItems_NoDueDate, null),
            new(Translator.ToDoPage_DuePresetToday, today),
            new(Translator.ToDoPage_DuePresetTomorrow, today.AddDays(1)),
            new(Translator.ToDoPage_DuePresetNextWeek, today.AddDays(7))
        };

        if (suggested?.Date is { } date && options.All(option => option.Date != date))
            options.Insert(1, new TaskDueOption(date.ToString("D", CultureInfo.CurrentCulture), date));

        return [.. options];
    }

    private void CreateClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (SelectedList is not { } list || string.IsNullOrWhiteSpace(TaskTitle))
        {
            args.Cancel = true;
            return;
        }

        Result = new TaskFromItemResult(TaskTitle.Trim(), TaskNotes ?? string.Empty, list.List, SelectedDue?.Date);
    }
}
