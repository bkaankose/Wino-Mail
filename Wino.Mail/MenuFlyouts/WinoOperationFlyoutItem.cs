using System;

using Wino.Controls;
using Wino.Helpers;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Menus;
using Wino.Domain.Models.Folders;




#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#else
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
#endif

namespace Wino.MenuFlyouts
{
    public class WinoOperationFlyoutItem<TOperationMenuItem> : MenuFlyoutItem, IDisposable where TOperationMenuItem : IMenuOperation
    {
        private const double CustomHeight = 35;

        public TOperationMenuItem Operation { get; set; }
        Action<TOperationMenuItem> Clicked { get; set; }

        public WinoOperationFlyoutItem(TOperationMenuItem operationMenuItem, Action<TOperationMenuItem> clicked)
        {
            Margin = new Thickness(4, 2, 4, 2);
            CornerRadius = new CornerRadius(6, 6, 6, 6);

            MinHeight = CustomHeight;

            Operation = operationMenuItem;
            IsEnabled = operationMenuItem.IsEnabled;

            if (Operation is FolderOperationMenuItem folderOperationMenuItem)
            {
                var internalOperation = folderOperationMenuItem.Operation;

                Icon = new WinoFontIcon() { Icon = XamlHelpers.GetPathGeometry(internalOperation) };
                Text = XamlHelpers.GetOperationString(internalOperation);
            }
            else if (Operation is MailOperationMenuItem mailOperationMenuItem)
            {
                var internalOperation = mailOperationMenuItem.Operation;

                Icon = new WinoFontIcon() { Icon = XamlHelpers.GetWinoIconGlyph(internalOperation) };
                Text = XamlHelpers.GetOperationString(internalOperation);
            }

            Clicked = clicked;
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
