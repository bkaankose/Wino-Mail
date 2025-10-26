using Microsoft.UI.Xaml;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoListView : Microsoft.UI.Xaml.Controls.ListView
{
    public bool IsAllSelected => Items.Count == SelectedItems.Count;

    protected override DependencyObject GetContainerForItemOverride() => new WinoListViewItem();

    public bool SelectMailItemContainer(MailItemViewModel mailItemViewModel)
    {
        WinoListViewItem? itemContainer = null;

        foreach (var item in Items)
        {
            if (item is MailItemViewModel mailItem && mailItem.Id == mailItemViewModel.Id)
            {
                itemContainer = ContainerFromItem(mailItemViewModel) as WinoListViewItem;

                break;
            }
            else if (item is ThreadMailItemViewModel threadMailItemViewModel && threadMailItemViewModel.HasUniqueId(mailItemViewModel.MailCopy.UniqueId))
            {
                itemContainer = ContainerFromItem(threadMailItemViewModel) as WinoListViewItem;

                // Try to get the inner WinoListView.
                if (itemContainer != null)
                {
                    itemContainer.IsExpanded = true;

                    var innerListViewControl = itemContainer.GetWinoListViewControl();

                    if (innerListViewControl != null)
                    {
                        itemContainer = innerListViewControl.ContainerFromItem(mailItemViewModel) as WinoListViewItem;
                    }
                }

                break;
            }
        }

        itemContainer?.IsSelected = true;

        return itemContainer != null;
    }
}
