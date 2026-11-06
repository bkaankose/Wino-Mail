using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class ExchangeSettingsPage : ExchangeSettingsPageAbstract
{
    public ExchangeSettingsPage()
    {
        InitializeComponent();
    }

    private void FieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.IsPrimaryVisible || !ViewModel.PrimaryCommand.CanExecute(null))
            return;

        e.Handled = true;
        ViewModel.PrimaryCommand.Execute(null);
    }
}
