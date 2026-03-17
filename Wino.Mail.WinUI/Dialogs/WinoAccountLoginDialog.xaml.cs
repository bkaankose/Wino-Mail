using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Services;

namespace Wino.Dialogs;

public sealed partial class WinoAccountLoginDialog : ContentDialog
{
    private readonly IWinoAccountProfileService _profileService;

    public WinoAccountLoginDialog(IWinoAccountProfileService profileService)
    {
        _profileService = profileService;
        InitializeComponent();
    }

    public WinoAccount? Result { get; private set; }

    private async void LoginClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;

        var validationError = ValidateInput();
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            ShowError(validationError);
            return;
        }

        var deferral = args.GetDeferral();

        try
        {
            await PerformLoginAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async System.Threading.Tasks.Task PerformLoginAsync()
    {
        var validationError = ValidateInput();
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            ShowError(validationError);
            return;
        }

        try
        {
            SetBusyState(true);
            HideError();

            var result = await _profileService.LoginAsync(EmailTextBox.Text.Trim(), PasswordBox.Password);

            if (!result.IsSuccess || result.Account == null)
            {
                ShowError(WinoAccountAuthErrorTranslator.Translate(result.ErrorCode));
                return;
            }

            Result = result.Account;
            Hide();
        }
        finally
        {
            SetBusyState(false);
        }
    }

    private string ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(EmailTextBox.Text))
        {
            return Translator.WinoAccount_Validation_EmailRequired;
        }

        if (string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            return Translator.WinoAccount_Validation_PasswordRequired;
        }

        return string.Empty;
    }

    private void EmailTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            PasswordBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private async void PasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await PerformLoginAsync();
        }
    }

    private void InputChanged(TextBox sender, TextBoxTextChangingEventArgs args) => HideError();

    private void InputChanged(object sender, RoutedEventArgs e) => HideError();

    private void SetBusyState(bool isBusy)
    {
        IsPrimaryButtonEnabled = !isBusy;
        IsSecondaryButtonEnabled = !isBusy;
        BusyRing.IsActive = isBusy;
        BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorTextBlock.Text = string.Empty;
        ErrorTextBlock.Visibility = Visibility.Collapsed;
    }
}
