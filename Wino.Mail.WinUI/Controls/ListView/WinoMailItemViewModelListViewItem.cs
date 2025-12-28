using System.ComponentModel;
using System.Diagnostics;
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
        Debug.WriteLine("WinoMailItemViewModelListViewItem item changed");

        if (e.OldValue is MailItemViewModel oldMailItemViewModel) UnregisterPropertyChanged(oldMailItemViewModel);
        if (e.NewValue is MailItemViewModel newMailItemViewModel) RegisterPropertyChanged(newMailItemViewModel);
    }

    private void RegisterPropertyChanged(MailItemViewModel model) => model.PropertyChanged += ModelPropertyChanged;
    private void UnregisterPropertyChanged(MailItemViewModel model) => model.PropertyChanged -= ModelPropertyChanged;

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MailItemViewModel mailItemViewModel) return;

        if (e.PropertyName == nameof(MailItemViewModel.IsSelected))
        {
            IsCustomSelected = mailItemViewModel.IsSelected;
        }
    }
}
