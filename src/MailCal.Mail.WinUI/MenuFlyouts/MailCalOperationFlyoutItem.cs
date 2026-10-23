using System;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Core.Domain.Models.Menus;
using MailCal.Helpers;
using MailCal.Mail.WinUI.Controls;

namespace MailCal.MenuFlyouts;

public partial class MailCalOperationFlyoutItem<TOperationMenuItem> : MenuFlyoutItem, IDisposable where TOperationMenuItem : IMenuOperation
{
    public TOperationMenuItem Operation { get; set; }
    Action<TOperationMenuItem> Clicked { get; set; }

    public MailCalOperationFlyoutItem(TOperationMenuItem operationMenuItem, Action<TOperationMenuItem> clicked)
    {
        Operation = operationMenuItem;
        IsEnabled = operationMenuItem.IsEnabled;

        MenuFlyoutLanguageHelper.Apply(this);

        if (Operation is FolderOperationMenuItem folderOperationMenuItem)
        {
            var internalOperation = folderOperationMenuItem.Operation;

            Icon = new MailCalFontIcon() { Icon = XamlHelpers.GetPathGeometry(internalOperation) };
            Text = XamlHelpers.GetOperationString(internalOperation);
        }
        else if (Operation is MailOperationMenuItem mailOperationMenuItem)
        {
            var internalOperation = mailOperationMenuItem.Operation;

            Icon = new MailCalFontIcon() { Icon = XamlHelpers.GetMailCalIconGlyph(internalOperation) };
            Text = XamlHelpers.GetOperationString(internalOperation);
        }

        Clicked = clicked;
        Click += MenuClicked;
    }

    private void MenuClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Clicked(Operation);
    }

    public void Dispose()
    {
        Click -= MenuClicked;
    }
}
