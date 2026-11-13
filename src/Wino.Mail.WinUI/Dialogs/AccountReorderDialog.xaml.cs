using System;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Dialogs;

/// <summary>
/// Orders groups and accounts at the top level, and the accounts inside each group.
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
}
