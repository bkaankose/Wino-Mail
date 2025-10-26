using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoListViewItem : ListViewItem
{
    public bool IsExpanded
    {
        get { return (bool)GetValue(IsExpandedProperty); }
        set { SetValue(IsExpandedProperty, value); }
    }

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(nameof(IsExpanded), typeof(bool), typeof(WinoListViewItem), new PropertyMetadata(false, OnIsExpandedChanged));

    public WinoListViewItem()
    {
        DefaultStyleKey = typeof(WinoListViewItem);

        RegisterPropertyChangedCallback(IsSelectedProperty, OnIsSelectedChanged);
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WinoListViewItem item)
        {
            // Handle expansion state change if needed
        }
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (oldContent is IMailListItem oldMailItem)
        {
            UnregisterSelectionCallback(oldMailItem);
        }

        if (newContent is IMailListItem newMailItem)
        {
            IsSelected = newMailItem.IsSelected;
            RegisterSelectionCallback(newMailItem);
        }
    }

    private void UnregisterSelectionCallback(IMailListItem mailItem)
    {
        mailItem.PropertyChanged -= MailPropChanged;
    }

    private void RegisterSelectionCallback(IMailListItem mailItem)
    {
        mailItem.PropertyChanged += MailPropChanged;
    }

    // From model
    private void MailPropChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not IMailListItem mailItem) return;

        if (e.PropertyName == nameof(IMailListItem.IsSelected)) ApplySelectionForContainer(mailItem);
    }

    // From container.
    private void OnIsSelectedChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (Content is IMailListItem mailItem)
        {
            ApplySelectionForModel(mailItem);
        }
    }

    private void ApplySelectionForModel(IMailListItem mailItem)
    {
        if (mailItem.IsSelected != IsSelected)
        {
            mailItem.IsSelected = IsSelected;
            WeakReferenceMessenger.Default.Send(new SelectedItemsChangedMessage());
        }
    }

    private void ApplySelectionForContainer(IMailListItem mailItem)
    {
        if (IsSelected != mailItem.IsSelected)
        {
            IsSelected = mailItem.IsSelected;
        }
    }

    public WinoListView? GetWinoListViewControl()
    {
        var expander = GetTemplateChild("ExpanderPart") as Expander;

        if (expander?.Content is ContentPresenter presenter) return VisualTreeHelper.GetChild(presenter, 0) as WinoListView;

        return null;
    }
}
