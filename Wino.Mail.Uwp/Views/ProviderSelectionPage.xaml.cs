using Windows.UI.Xaml.Controls;
using Wino.Mail.Uwp.Views.Abstract;

namespace Wino.Views;

public sealed partial class ProviderSelectionPage : ProviderSelectionPageAbstract
{
    public ProviderSelectionPage()
    {
        InitializeComponent();
    }

    private void ProviderSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not ListView listView) return;
        if (listView.SelectedItem == null) return;

        ViewModel.SelectedProvider = listView.SelectedItem as Wino.Core.Domain.Interfaces.IProviderDetail;
    }

}
