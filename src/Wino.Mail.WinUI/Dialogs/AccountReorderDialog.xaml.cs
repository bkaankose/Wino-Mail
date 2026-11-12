using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using WinRT;

namespace Wino.Dialogs;

/// <summary>
/// Orders groups, linked accounts and accounts at the top level, and the entries inside each group.
/// The dialog never changes group membership; the dialog service saves the order when Save is chosen.
/// </summary>
public sealed partial class AccountReorderDialog : ContentDialog
{
    internal ObservableCollection<AccountReorderItem> Items { get; }

    public AccountReorderDialog(ObservableCollection<AccountReorderItem> items)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));

        InitializeComponent();
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AccountReorderItem item })
            AccountReorderItem.Move(Items, item, -1);
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: AccountReorderItem item })
            AccountReorderItem.Move(Items, item, 1);
    }
}
