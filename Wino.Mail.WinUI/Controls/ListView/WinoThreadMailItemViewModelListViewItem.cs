using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Controls;
using Wino.Helpers;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoThreadMailItemViewModelListViewItem : ListViewItem
{
    public bool IsThreadExpanded
    {
        get { return (bool)GetValue(IsThreadExpandedProperty); }
        set { SetValue(IsThreadExpandedProperty, value); }
    }

    public static readonly DependencyProperty IsThreadExpandedProperty = DependencyProperty.Register(nameof(IsThreadExpanded), typeof(bool), typeof(WinoThreadMailItemViewModelListViewItem), new PropertyMetadata(false, new PropertyChangedCallback(OnIsThreadExpandedChanged)));

    private readonly long _selectionChangeCallbackToken;
    public WinoThreadMailItemViewModelListViewItem()
    {
        DefaultStyleKey = typeof(WinoThreadMailItemViewModelListViewItem);

        _selectionChangeCallbackToken = RegisterPropertyChangedCallback(IsSelectedProperty, OnIsSelectedChanged);
    }

    public void Cleanup()
    {
        if (Content is ThreadMailItemViewModel mailItem)
        {
            UnregisterSelectionCallback(mailItem);

            UnregisterPropertyChangedCallback(IsSelectedProperty, _selectionChangeCallbackToken);
        }
    }

    private static void OnIsThreadExpandedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs dp)
    {
        // 1. Reflect expansion changes to WinoExpander.
        // 2. Automatically select first item on expansion, if none selected.
        // 3. Unselect all items on collapse.

        var control = sender as WinoThreadMailItemViewModelListViewItem;

        var innerControl = control?.GetWinoListViewControl();
        var expander = control?.GetExpander();

        if (innerControl == null || control == null || expander == null) return;

        // 1
        expander.IsExpanded = control.IsThreadExpanded;

        // 2
        if (control.IsThreadExpanded && innerControl.SelectedItems.Count == 0 && innerControl.Items.Count > 0)
        {
            innerControl.SelectedItem = innerControl.Items[0];
        }

        // 3
        if (!control.IsSelected) innerControl?.SelectedItems.Clear();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (oldContent is ThreadMailItemViewModel oldMailItem)
        {
            UnregisterSelectionCallback(oldMailItem);
        }

        if (newContent is ThreadMailItemViewModel newMailItem)
        {
            IsSelected = newMailItem.IsSelected;
            RegisterSelectionCallback(newMailItem);
        }
    }

    private void OnIsSelectedChanged(DependencyObject sender, DependencyProperty dp)
    {
        IsThreadExpanded = IsSelected;
    }

    public void UnregisterSelectionCallback(ThreadMailItemViewModel mailItem)
    {
        mailItem.PropertyChanged -= MailPropChanged;
    }

    private void MailPropChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ThreadMailItemViewModel mailItem) return;

        if (e.PropertyName == nameof(ThreadMailItemViewModel.IsThreadExpanded))
        {
            ApplySelectionForContainer(mailItem);
        }
    }

    private void RegisterSelectionCallback(ThreadMailItemViewModel mailItem)
    {
        mailItem.PropertyChanged += MailPropChanged;
    }

    private void ApplySelectionForModel(ThreadMailItemViewModel mailItem)
    {
        if (mailItem.IsThreadExpanded != IsThreadExpanded)
        {
            mailItem.IsThreadExpanded = IsThreadExpanded;
        }
    }

    private void ApplySelectionForContainer(ThreadMailItemViewModel mailItem)
    {
        if (IsThreadExpanded != mailItem.IsThreadExpanded) IsThreadExpanded = mailItem.IsThreadExpanded;
    }

    public WinoListView? GetWinoListViewControl()
    {
        var expander = GetExpander();

        if (expander?.Content is WinoListView control) return control;

        return null;
    }

    public WinoExpander? GetExpander() => WinoVisualTreeHelper.FindDescendants<WinoExpander>(this).FirstOrDefault();
}
