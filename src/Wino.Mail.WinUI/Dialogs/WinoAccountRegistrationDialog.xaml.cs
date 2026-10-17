using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.WinUI.Services;

namespace Wino.Dialogs;

public sealed partial class WinoAccountRegistrationDialog : ContentDialog
{
    private readonly IWinoAccountProfileService _profileService;
    private readonly IWinoAccountExternalSignInService? _externalSignIn;
    private CancellationTokenSource? _externalSignInCancellation;

    public WinoAccountRegistrationDialog(IWinoAccountProfileService profileService, IWinoAccountExternalSignInService? externalSignIn = null)
    {
        _profileService = profileService;
        _externalSignIn = externalSignIn;
        InitializeComponent();

        AppleSignInButton.Visibility = externalSignIn is { IsAppleAvailable: true } ? Visibility.Visible : Visibility.Collapsed;
        GoogleSignInButton.Visibility = externalSignIn is { IsGoogleAvailable: true } ? Visibility.Visible : Visibility.Collapsed;
        ExternalSignInPanel.Visibility = externalSignIn is { IsAppleAvailable: true } or { IsGoogleAvailable: true }
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>The signed-in account when Sign in with Apple or Google created or found it; null for the e-mail form, which ends in a confirmation mail.</summary>
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

    private async void AppleSignInButton_Click(object sender, RoutedEventArgs e)
        => await SignInExternallyAsync(token => _externalSignIn!.SignInWithAppleAsync(token));

    private async void GoogleSignInButton_Click(object sender, RoutedEventArgs e)
        => await SignInExternallyAsync(token => _externalSignIn!.SignInWithGoogleAsync(token));

    /// <summary>
    /// The API browser flow: the default browser opens the provider's page and the wino-mail://auth-complete redirect
    /// comes back through protocol activation. The provider both registers and signs in. Cancel stays enabled so the
    /// user can abandon the wait; a dismissed or cancelled flow shows nothing.
    /// </summary>
    private async Task SignInExternallyAsync(Func<CancellationToken, Task<WinoAccountOperationResult>> signIn)
    {
        if (_externalSignIn is null || _externalSignInCancellation is not null) return;

        var cancellation = new CancellationTokenSource();
        _externalSignInCancellation = cancellation;

        try
        {
            SetExternalSignInBusyState(true);
            HideError();

            var result = await signIn(cancellation.Token);

            if (result.IsSuccess && result.Account is not null)
            {
                Result = result.Account;
                Hide();
                return;
            }

            if (!cancellation.IsCancellationRequested && !WinoAccountClientErrorCodes.IsCancelled(result.ErrorCode))
            {
                ShowError(WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage));
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed while the browser flow was waiting.
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _externalSignInCancellation = null;
            cancellation.Dispose();
            SetExternalSignInBusyState(false);
        }
    }

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        => _externalSignInCancellation?.Cancel();

    private async Task PerformRegistrationAsync()
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
        ExternalSignInPanel.IsEnabled = !isBusy;
        BusyRing.IsActive = isBusy;
        BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetExternalSignInBusyState(bool isBusy)
    {
        IsPrimaryButtonEnabled = !isBusy;
        ExternalSignInPanel.IsEnabled = !isBusy;
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
