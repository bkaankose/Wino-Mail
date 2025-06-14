using System;
using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;
using Wino.Core.UWP.Controls;
using Wino.Helpers;

namespace Wino.MenuFlyouts;

public partial class WinoOperationFlyoutItem<TOperationMenuItem> : MenuFlyoutItem, IDisposable where TOperationMenuItem : IMenuOperation
{
    public TOperationMenuItem Operation { get; set; }
    Action<TOperationMenuItem> Clicked { get; set; }

    public WinoOperationFlyoutItem(TOperationMenuItem operationMenuItem, Action<TOperationMenuItem> clicked)
    {
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
