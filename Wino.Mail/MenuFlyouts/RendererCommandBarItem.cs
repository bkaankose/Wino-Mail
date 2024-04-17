using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Controls;
using Wino.Core.Domain.Enums;
using Wino.Helpers;

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

        private void MenuClicked(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Clicked(Operation);
        }

        public void Dispose()
        {
            Click -= MenuClicked;
        }
    }
}
