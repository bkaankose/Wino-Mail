using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.ContextFlyout;

internal sealed partial class WinoContextFlyoutResources : ResourceDictionary
{
    public WinoContextFlyoutResources()
    {
        InitializeComponent();
    }

    public DataTemplateSelector TemplateSelector =>
        (DataTemplateSelector)this[nameof(TemplateSelector)];

    public DataTemplate HeaderItemTemplate =>
        (DataTemplate)this[nameof(HeaderItemTemplate)];
}
