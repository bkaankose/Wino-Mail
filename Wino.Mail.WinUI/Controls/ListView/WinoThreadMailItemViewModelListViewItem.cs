using System.ComponentModel;
using System.Diagnostics;
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
        Debug.WriteLine("WinoMailItemViewModelListViewItem item changed");

        if (e.OldValue is ThreadMailItemViewModel oldMailItemViewModel) UnregisterPropertyChanged(oldMailItemViewModel);
        if (e.NewValue is ThreadMailItemViewModel newMailItemViewModel) RegisterPropertyChanged(newMailItemViewModel);
    }

    private void RegisterPropertyChanged(ThreadMailItemViewModel model) => model.PropertyChanged += ModelPropertyChanged;
    private void UnregisterPropertyChanged(ThreadMailItemViewModel model) => model.PropertyChanged -= ModelPropertyChanged;

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ThreadMailItemViewModel mailItemViewModel) return;

        if (e.PropertyName == nameof(ThreadMailItemViewModel.IsSelected))
        {
            IsCustomSelected = mailItemViewModel.IsSelected;
        }
    }
}
