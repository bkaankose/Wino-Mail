using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemViewModelListViewItem : ListViewItem
{
    public WinoMailItemViewModelListViewItem()
    {
        DefaultStyleKey = typeof(WinoMailItemViewModelListViewItem);

        RegisterPropertyChangedCallback(IsSelectedProperty, OnIsSelectedChanged);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (oldContent is MailItemViewModel oldMailItem)
        {
            UnregisterSelectionCallback(oldMailItem);
        }

        if (newContent is MailItemViewModel newMailItem)
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
        if (sender is not MailItemViewModel mailItem) return;

        if (e.PropertyName == nameof(MailItemViewModel.IsSelected)) ApplySelectionForContainer(mailItem);
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
}
