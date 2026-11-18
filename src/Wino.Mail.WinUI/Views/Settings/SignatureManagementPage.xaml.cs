using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Mail;
using Wino.Mail.WinUI.Views.Abstract;
using WinRT;

namespace Wino.Mail.WinUI.Views.Settings;

public sealed partial class SignatureManagementPage : SignatureManagementPageAbstract
{
    public SignatureManagementPage()
    {
        InitializeComponent();
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void EditSignature_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is AccountSignature signature)
        {
            ViewModel.OpenSignatureEditorEditCommand.Execute(signature);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void DeleteSignature_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is AccountSignature signature)
        {
            ViewModel.DeleteSignatureCommand.Execute(signature);
        }
    }
}
