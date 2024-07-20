using System;

using Wino.Controls;
using Wino.Core.Domain.Enums;
using Wino.Helpers;

#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#else
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#endif
namespace Wino.MenuFlyouts
{
    public class RendererCommandBarItem : AppBarButton, IDisposable
    {
        public MailOperation Operation { get; set; }
        Action<MailOperation> Clicked { get; set; }

        public RendererCommandBarItem(MailOperation operation, Action<MailOperation> clicked)
        {
            Margin = new Thickness(6, 0, 6, 0);
            CornerRadius = new CornerRadius(6);

            Operation = operation;
            Clicked = clicked;

            Label = XamlHelpers.GetOperationString(operation);
            Icon = new WinoFontIcon() { Icon = WinoIconGlyph.Archive };

            Click += MenuClicked;
        }

        private void MenuClicked(object sender, RoutedEventArgs e)
        {
            Clicked(Operation);
        }

        public void Dispose()
        {
            Click -= MenuClicked;
        }
    }
}
