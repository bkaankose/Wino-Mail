using Microsoft.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace Wino.Controls
{
    public class WinoExpander : Expander
    {
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (GetTemplateChild("ExpanderHeader") is ToggleButton toggleButton)
            {
                toggleButton.Padding = new Windows.UI.Xaml.Thickness(0, 4, 0, 4);
            }
        }
    }
}
