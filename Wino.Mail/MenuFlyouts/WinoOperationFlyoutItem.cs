using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;
using Wino.Helpers;

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
