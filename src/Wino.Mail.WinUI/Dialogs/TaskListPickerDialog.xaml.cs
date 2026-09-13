using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Dialogs;

public sealed record TaskListPickerItem(AccountTaskList List, MailAccount? Account)
{
    public string AccountDisplayText
    {
        get
        {
            var name = Account?.Name?.Trim() ?? string.Empty;
            var address = Account?.Address?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(name))
                return address;
            if (string.IsNullOrEmpty(address))
                return name;

            return $"{name} · {address}";
        }
    }

    public string AutomationName => string.IsNullOrEmpty(AccountDisplayText)
        ? List.Title
        : $"{List.Title}, {AccountDisplayText}";
}

public sealed partial class TaskListPickerDialog : ContentDialog
{
    public IReadOnlyList<TaskListPickerItem> Items { get; }
    public AccountTaskList? PickedList { get; private set; }

    public TaskListPickerDialog(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts)
    {
        Items = taskLists
            .Select(list => new TaskListPickerItem(
                list,
                accounts.FirstOrDefault(account => account.Id == list.MailAccountId)))
            .ToList();
        InitializeComponent();
    }

    private void ItemClicked(object sender, ItemClickEventArgs e)
    {
        PickedList = (e.ClickedItem as TaskListPickerItem)?.List;
        Hide();
    }
}
