using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.WinUI.Controls.Advanced;

public partial class WinoItemsView : ItemsView
{
    public IEnumerable<object>? CastedItemsSource => ItemsSource as IEnumerable<object>;

    public WinoItemsView()
    {
        DefaultStyleKey = typeof(ItemsView);
    }
}
