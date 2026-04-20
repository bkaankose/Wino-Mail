using Microsoft.UI.Xaml.Controls;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Views;

public sealed partial class ProviderSelectionPage : ProviderSelectionPageAbstract
{
    public ProviderSelectionPage()
    {
        InitializeComponent();
    }

    private void ProviderSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem == null) return;

        ViewModel.SelectedProvider = sender.SelectedItem as Wino.Core.Domain.Interfaces.IProviderDetail;
    }

    private void AccountColorGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
        {
            AccountColorFlyout.Hide();
        }
    }
}
