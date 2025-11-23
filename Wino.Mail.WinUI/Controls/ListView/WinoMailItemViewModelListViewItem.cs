using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemViewModelListViewItem : ListViewItem
{
    [GeneratedDependencyProperty]
    public partial MailItemViewModel? Item { get; set; }

    public WinoMailItemViewModelListViewItem()
    {
        DefaultStyleKey = typeof(WinoMailItemViewModelListViewItem);
    }
}
