using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.WinUI.Services;

namespace Wino.Dialogs;

public sealed partial class WinoAccountLoginDialog : ContentDialog
{
    private readonly IWinoAccountProfileService _profileService;
    private bool _isForgotPasswordMode;

    public WinoAccountLoginDialog(IWinoAccountProfileService profileService)
    {
        _profileService = profileService;
        InitializeComponent();
        UpdateMode();
    }

    public WinoAccount? Result { get; private set; }
    public string? PendingConfirmationEmailAddress { get; private set; }
    public EmailConfirmationRequiredDetailsDto? EmailConfirmationRequiredDetails { get; private set; }
    public string? PasswordResetEmailAddress { get; private set; }

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
            await PerformPrimaryActionAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async System.Threading.Tasks.Task PerformPrimaryActionAsync()
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

            if (_isForgotPasswordMode)
            {
                var forgotPasswordResponse = await _profileService.ForgotPasswordAsync(EmailTextBox.Text.Trim());
                if (!forgotPasswordResponse.IsSuccess)
                {
                    ShowError(WinoAccountAuthErrorTranslator.Translate(forgotPasswordResponse.ErrorCode));
                    return;
                }

                PasswordResetEmailAddress = EmailTextBox.Text.Trim();
                Hide();
                return;
            }

            var result = await _profileService.LoginAsync(EmailTextBox.Text.Trim(), PasswordBox.Password);

            if (!result.IsSuccess || result.Account == null)
            {
                var confirmationDetails = WinoAccountEmailConfirmationHelper.Parse(result.ErrorDetails);
                if (WinoAccountEmailConfirmationHelper.IsEmailConfirmationRequiredError(result.ErrorCode) && confirmationDetails != null)
                {
                    PendingConfirmationEmailAddress = EmailTextBox.Text.Trim();
                    EmailConfirmationRequiredDetails = confirmationDetails;
                    Hide();
                    return;
                }

                ShowError(WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage));
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

        if (_isForgotPasswordMode)
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            return Translator.WinoAccount_Validation_PasswordRequired;
        }

        return string.Empty;
    }

    private async void EmailTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (_isForgotPasswordMode)
            {
                e.Handled = true;
                await PerformPrimaryActionAsync();
                return;
            }

            PasswordBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private async void PasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await PerformPrimaryActionAsync();
        }
    }

    private void ModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isForgotPasswordMode = !_isForgotPasswordMode;
        PasswordBox.Password = string.Empty;
        HideError();
        UpdateMode();
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

    private void UpdateMode()
    {
        Title = _isForgotPasswordMode
            ? Translator.WinoAccount_ForgotPasswordDialog_Title
            : Translator.WinoAccount_LoginDialog_Title;

        PrimaryButtonText = _isForgotPasswordMode
            ? Translator.WinoAccount_ForgotPasswordDialog_PrimaryButton
            : Translator.Buttons_SignIn;

        BenefitsPanel.Visibility = _isForgotPasswordMode ? Visibility.Collapsed : Visibility.Visible;
        PasswordPanel.Visibility = _isForgotPasswordMode ? Visibility.Collapsed : Visibility.Visible;
        ForgotPasswordInfoPanel.Visibility = _isForgotPasswordMode ? Visibility.Visible : Visibility.Collapsed;
        ModeToggleButton.Content = _isForgotPasswordMode
            ? Translator.WinoAccount_ForgotPasswordDialog_BackToSignIn
            : Translator.WinoAccount_LoginDialog_ForgotPasswordLink;
    }
}
