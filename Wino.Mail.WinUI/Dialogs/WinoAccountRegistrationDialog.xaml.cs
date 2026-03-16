using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Services;

namespace Wino.Dialogs;

public sealed partial class WinoAccountRegistrationDialog : ContentDialog
{
    private readonly IWinoAccountProfileService _profileService;

    public WinoAccountRegistrationDialog(IWinoAccountProfileService profileService)
    {
        _profileService = profileService;
        InitializeComponent();
    }

    public WinoAccount? Result { get; private set; }

    private async void RegisterClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
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
            SetBusyState(true);
            HideError();

            var result = await _profileService.RegisterAsync(EmailTextBox.Text.Trim(), PasswordBox.Password);

            if (!result.IsSuccess || result.Account == null)
            {
                ShowError(WinoAccountAuthErrorTranslator.Translate(result.ErrorCode));
                return;
            }

            Result = result.Account;
            args.Cancel = false;
            Hide();
        }
        finally
        {
            SetBusyState(false);
            deferral.Complete();
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

        if (!string.Equals(PasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            return Translator.WinoAccount_Validation_PasswordMismatch;
        }

        return string.Empty;
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
