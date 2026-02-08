using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using WinRT;

namespace Wino.Mail.WinUI.Controls.ListView;

[GeneratedBindableCustomProperty]
public partial class WinoMailItemViewModelListViewItem : ListViewItem
{
    [GeneratedDependencyProperty]
    public partial MailItemViewModel? Item { get; set; }

    [GeneratedDependencyProperty]
    public partial bool IsCustomSelected { get; set; }

    public WinoMailItemViewModelListViewItem()
    {
        DefaultStyleKey = typeof(WinoMailItemViewModelListViewItem);
    }

    partial void OnItemPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MailItemViewModel oldItem)
            oldItem.OnSelectionChanged = null;

        if (e.NewValue is MailItemViewModel newItem)
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
