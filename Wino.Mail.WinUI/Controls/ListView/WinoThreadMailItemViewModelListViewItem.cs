using System.Linq;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Controls;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;
using WinRT;

namespace Wino.Mail.WinUI.Controls.ListView;

[GeneratedBindableCustomProperty]
public partial class WinoThreadMailItemViewModelListViewItem : ListViewItem
{
    [GeneratedDependencyProperty]
    public partial bool IsThreadExpanded { get; set; }

    [GeneratedDependencyProperty]
    public partial ThreadMailItemViewModel? Item { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsCustomSelected { get; set; }

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

    partial void OnItemPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ThreadMailItemViewModel oldItem)
            oldItem.OnSelectionChanged = null;

        if (e.NewValue is ThreadMailItemViewModel newItem)
        {
            newItem.OnSelectionChanged = (selected) => IsCustomSelected = selected;
            IsCustomSelected = newItem.IsSelected;
        }
        else
        {
            IsCustomSelected = false;
        }
    }
}
