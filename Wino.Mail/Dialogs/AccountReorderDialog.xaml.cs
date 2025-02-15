using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;

namespace Wino.Dialogs;

public sealed partial class AccountReorderDialog : ContentDialog
{
    public ObservableCollection<IAccountProviderDetailViewModel> Accounts { get; }

    private int count;
    private bool isOrdering = false;

    private readonly IAccountService _accountService = App.Current.Services.GetService<IAccountService>();

    public AccountReorderDialog(ObservableCollection<IAccountProviderDetailViewModel> accounts)
    {
        Accounts = accounts;

        count = accounts.Count;

        InitializeComponent();
    }

    private void DialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        Accounts.CollectionChanged -= AccountsChanged;
        Accounts.CollectionChanged += AccountsChanged;
    }

    private void DialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => Accounts.CollectionChanged -= AccountsChanged;

    private async void AccountsChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (count - 1 == Accounts.Count)
            isOrdering = true;

        if (count == Accounts.Count && isOrdering)
        {
            // Order is completed. Apply changes.

            var dict = Accounts.ToDictionary(a => a.StartupEntityId, a => Accounts.IndexOf(a));

            await _accountService.UpdateAccountOrdersAsync(dict);

            isOrdering = false;
        }
    }
}
