using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
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
    public string? ConfirmationEmailAddress { get; private set; }

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
            await PerformRegistrationAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async System.Threading.Tasks.Task PerformRegistrationAsync()
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

            var result = await _profileService.RegisterWithProfileAsync(EmailTextBox.Text.Trim(), PasswordBox.Password, DisplayNameTextBox.Text.Trim());

            if (!result.IsSuccess || result.Account == null)
            {
                ShowError(WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage));
                return;
            }

            ConfirmationEmailAddress = result.Account.Email;
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

        if (!string.Equals(PasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            return Translator.WinoAccount_Validation_PasswordMismatch;
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

    private void PasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            ConfirmPasswordBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private async void ConfirmPasswordBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await PerformRegistrationAsync();
        }
    }

    private async void PrivacyPolicyLink_Click(object sender, RoutedEventArgs e)
    {
        await Launcher.LaunchUriAsync(new Uri(AppUrls.PrivacyPolicy));
    }

    private void InputChanged(TextBox sender, TextBoxTextChangingEventArgs args) => HideError();

    private void InputChanged(object sender, RoutedEventArgs e) => HideError();

    private void SetBusyState(bool isBusy)
    {
        DisplayNameTextBox.IsEnabled = !isBusy;
        IsPrimaryButtonEnabled = !isBusy;
        IsSecondaryButtonEnabled = !isBusy;
        BusyRing.IsActive = isBusy;
        BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Visibility.Visible;
        FrameworkElementAutomationPeer.FromElement(ErrorTextBlock)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void HideError()
    {
        ErrorTextBlock.Text = string.Empty;
        ErrorTextBlock.Visibility = Visibility.Collapsed;
    }
}
