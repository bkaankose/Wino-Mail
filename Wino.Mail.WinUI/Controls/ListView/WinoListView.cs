using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoListView : Microsoft.UI.Xaml.Controls.ListView
{
    public bool IsAllSelected => Items.Count == SelectedItems.Count;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        if (item is MailItemViewModel mailItemViewModel && element is WinoMailItemViewModelListViewItem container)
        {
            // Ensure the container's selection state matches the model's state
            // This is crucial for virtualization scenarios where containers are recycled

            container.IsSelected = mailItemViewModel.IsSelected;
        }
        else if (item is ThreadMailItemViewModel threadMailItemViewModel && element is WinoThreadMailItemViewModelListViewItem threadContainer)
        {
            threadContainer.IsThreadExpanded = threadMailItemViewModel.IsThreadExpanded;
        }

        base.PrepareContainerForItemOverride(element, item);
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is WinoThreadMailItemViewModelListViewItem threadMailItemViewModelListViewItem) threadMailItemViewModelListViewItem.Cleanup();
        if (element is WinoMailItemViewModelListViewItem winoMailItemViewModelListViewItem) winoMailItemViewModelListViewItem.Cleanup();

        base.ClearContainerForItemOverride(element, item);
    }

    public bool SelectMailItemContainer(MailItemViewModel mailItemViewModel)
    {
        WinoMailItemViewModelListViewItem? itemContainer = null;
        WinoThreadMailItemViewModelListViewItem? threadContainer = null;

        foreach (var item in Items)
        {
            if (item is MailItemViewModel mailItem && mailItem.Id == mailItemViewModel.Id)
            {
                itemContainer = ContainerFromItem(mailItemViewModel) as WinoMailItemViewModelListViewItem;

                break;
            }
            else if (item is ThreadMailItemViewModel threadMailItemViewModel && threadMailItemViewModel.HasUniqueId(mailItemViewModel.MailCopy.UniqueId))
            {
                threadContainer = ContainerFromItem(threadMailItemViewModel) as WinoThreadMailItemViewModelListViewItem;

                // Try to get the inner WinoListView.
                if (threadContainer != null)
                {
                    threadContainer.IsThreadExpanded = true;

                    var innerListViewControl = threadContainer.GetWinoListViewControl();

                    if (innerListViewControl != null)
                    {
                        itemContainer = innerListViewControl.ContainerFromItem(mailItemViewModel) as WinoMailItemViewModelListViewItem;
                    }
                }

                break;
            }
        }

        if (itemContainer != null)
        {
            itemContainer.IsSelected = true;
            return true;
        }
        else if (threadContainer != null)
        {
            return true;
        }

        return false;
    }

    public void ChangeSelectionMode(ListViewSelectionMode mode)
    {
        // Not only this control, but also all inner WinoListView controls should change the selection mode.
        // TODO: New threads added after this call won't have the correct selection mode.

        SelectionMode = mode;

        foreach (var item in Items)
        {
            if (item is ThreadMailItemViewModel)
            {
                var itemContainer = ContainerFromItem(item) as WinoThreadMailItemViewModelListViewItem;
                if (itemContainer != null)
                {
                    var innerListViewControl = itemContainer.GetWinoListViewControl();
                    innerListViewControl?.ChangeSelectionMode(mode);
                }
            }
        }
    }
}
