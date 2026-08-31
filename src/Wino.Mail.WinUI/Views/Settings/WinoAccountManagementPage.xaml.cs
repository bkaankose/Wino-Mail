using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class WinoAccountManagementPage : WinoAccountManagementPageAbstract
{
    public WinoAccountManagementPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The view model already decides which offer opens first. The grid has no selection
    /// of its own until something selects one, so mirror that choice once it exists.
    /// </summary>
    private void BenefitsItemsViewLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ItemsView itemsView || ViewModel.SelectedBenefit is null)
        {
            return;
        }

        var index = ViewModel.Benefits.IndexOf(ViewModel.SelectedBenefit);
        if (index >= 0)
        {
            itemsView.Select(index);
        }
    }

    /// <summary>
    /// Below this width the 300 epx illustration would leave the copy in a strip too narrow
    /// to read and clip the call to action, so the art gives up its column.
    /// </summary>
    private const double BenefitDetailArtMinimumPanelWidth = 620;

    /// <summary>
    /// An AdaptiveTrigger would be the usual answer, but this panel lives inside an
    /// x:Load-deferred subtree where the trigger never attaches, so the panel watches its
    /// own width instead.
    /// </summary>
    private void BenefitDetailPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        BenefitDetailArtColumn.Visibility = e.NewSize.Width >= BenefitDetailArtMinimumPanelWidth
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BenefitSelectionChanged(ItemsView sender, ItemsViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is WinoAccountBenefitItemViewModel benefit)
        {
            ViewModel.SelectedBenefit = benefit;
        }
    }
}
