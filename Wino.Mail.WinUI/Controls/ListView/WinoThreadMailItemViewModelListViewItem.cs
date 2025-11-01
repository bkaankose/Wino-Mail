using System.Linq;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Controls;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoThreadMailItemViewModelListViewItem : ListViewItem
{
    [GeneratedDependencyProperty]
    public partial bool IsThreadExpanded { get; set; }

    [GeneratedDependencyProperty]
    public partial ThreadMailItemViewModel? Item { get; set; }


    public WinoThreadMailItemViewModelListViewItem()
    {
        DefaultStyleKey = typeof(WinoThreadMailItemViewModelListViewItem);
    }

    public WinoListView? GetWinoListViewControl()
    {
        var expander = GetExpander();

        if (expander?.Content is WinoListView control) return control;

        return null;
    }

    public WinoExpander? GetExpander() => WinoVisualTreeHelper.FindDescendants<WinoExpander>(this).FirstOrDefault();
}
