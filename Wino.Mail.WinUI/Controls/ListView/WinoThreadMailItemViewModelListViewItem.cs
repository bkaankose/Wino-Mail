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
    private WinoExpander? _expander;
    private WinoListView? _threadListView;

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
        if (_threadListView?.XamlRoot != null)
        {
            return _threadListView;
        }

        var expander = GetExpander();
        _threadListView = expander?.Content as WinoListView;

        return _threadListView;
    }

    public WinoExpander? GetExpander()
    {
        if (_expander?.XamlRoot != null)
        {
            return _expander;
        }

        _expander = WinoVisualTreeHelper.FindDescendants<WinoExpander>(this).FirstOrDefault();
        return _expander;
    }

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

        _expander = null;
        _threadListView = null;
    }
}
