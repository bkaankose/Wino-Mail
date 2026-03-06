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
        ViewModel.SelectedProvider = sender.SelectedItem as Wino.Core.Domain.Interfaces.IProviderDetail;
    }
}
