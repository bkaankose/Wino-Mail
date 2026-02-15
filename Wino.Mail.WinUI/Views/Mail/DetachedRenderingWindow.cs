using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Views.Mail;

public sealed class DetachedRenderingWindow : Window
{
    public DetachedRenderingWindow(UIElement content)
    {
        Title = "Wino Mail";

        Content = new Grid
        {
            Children =
            {
                content
            }
        };
    }
}
