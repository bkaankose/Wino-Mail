using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Dialogs;

public sealed partial class TaskListPickerDialog : ContentDialog
{
    public IReadOnlyList<AccountTaskList> TaskLists { get; }
    public AccountTaskList? PickedList { get; private set; }

    public TaskListPickerDialog(IReadOnlyList<AccountTaskList> taskLists)
    {
        TaskLists = taskLists;
        InitializeComponent();
    }

    private void ItemClicked(object sender, ItemClickEventArgs e)
    {
        PickedList = e.ClickedItem as AccountTaskList;
        Hide();
    }
}
