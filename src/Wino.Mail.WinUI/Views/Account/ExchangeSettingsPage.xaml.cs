using Microsoft.UI.Xaml.Controls;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class ExchangeSettingsPage : ExchangeSettingsPageAbstract
{
    public ExchangeSettingsPage()
    {
        InitializeComponent();
    }

    // PasswordBox has no two-way x:Bind for Password on all targets; mirror it into the view model.
    private void PasswordChanged(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            ViewModel.Password = passwordBox.Password;
    }
}
