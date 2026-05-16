using System.ComponentModel;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Helpers;
using WinRT;

namespace Wino.Mail.WinUI.Controls.ListView;

[GeneratedBindableCustomProperty]
public partial class WinoMailItemViewModelListViewItem : ListViewItem
{
    private INotifyPropertyChanged? _itemPropertySource;

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
        {
            oldItem.OnSelectionChanged = null;
        }

        if (_itemPropertySource != null)
        {
            _itemPropertySource.PropertyChanged -= ItemPropertyChanged;
            _itemPropertySource = null;
        }

        if (e.NewValue is MailItemViewModel newItem)
        {
            newItem.OnSelectionChanged = (selected) => IsCustomSelected = selected;
            IsCustomSelected = newItem.IsSelected;
            _itemPropertySource = newItem;
            _itemPropertySource.PropertyChanged += ItemPropertyChanged;
            UpdateAutomationName();
        }
        else
        {
            IsCustomSelected = false;
            AutomationProperties.SetName(this, string.Empty);
        }
    }

    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateAutomationName();

    private void UpdateAutomationName()
        => AutomationProperties.SetName(this, MailAccessibilityHelper.GetMailListItemName(Item));
}
