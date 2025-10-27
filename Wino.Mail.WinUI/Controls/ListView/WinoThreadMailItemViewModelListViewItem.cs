using System.Linq;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
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

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (newContent is ThreadMailItemViewModel threadMailItemViewModel) Item = threadMailItemViewModel;
    }

    public WinoThreadMailItemViewModelListViewItem()
    {
        DefaultStyleKey = typeof(WinoThreadMailItemViewModelListViewItem);
    }

    partial void OnIsThreadExpandedChanged(bool newValue)
    {
        // 1. Reflect expansion changes to WinoExpander.
        // 2. Automatically select first item on expansion, if none selected.
        // 3. Unselect all items on collapse.
    }

    private static void OnIsThreadExpandedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs dp)
    {
        // 1. Reflect expansion changes to WinoExpander.
        // 2. Automatically select first item on expansion, if none selected.
        // 3. Unselect all items on collapse.

        //var control = sender as WinoThreadMailItemViewModelListViewItem;

        //var innerControl = control?.GetWinoListViewControl();
        //var expander = control?.GetExpander();

        //if (innerControl == null || control == null || expander == null) return;


        //// 2
        //if (control.IsThreadExpanded && innerControl.SelectedItems.Count == 0 && innerControl.Items.Count > 0)
        //{
        //    innerControl.SelectedItems.Clear();

        //    // Make item selected, container might not be realized yet, so set on the model.
        //    // It'll appear selected when container is realized.

        //    var firstItem = innerControl.Items.FirstOrDefault() as MailItemViewModel;

        //    firstItem?.IsSelected = true;
        //}

        //// 1
        //expander.IsExpanded = control.IsThreadExpanded;

        //// 3
        //if (!control.IsSelected) innerControl?.SelectedItems.Clear();
    }

    public WinoListView? GetWinoListViewControl()
    {
        var expander = GetExpander();

        if (expander?.Content is WinoListView control) return control;

        return null;
    }

    public WinoExpander? GetExpander() => WinoVisualTreeHelper.FindDescendants<WinoExpander>(this).FirstOrDefault();
}
