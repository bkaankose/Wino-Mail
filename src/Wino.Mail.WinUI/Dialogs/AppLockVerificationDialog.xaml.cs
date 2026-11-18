#nullable enable
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI.Helpers;

namespace Wino.Dialogs;

/// <summary>Asks for the app lock PIN before a sensitive step such as turning the lock off.</summary>
public sealed partial class AppLockVerificationDialog : ContentDialog
{
    private readonly IAppLockService _appLockService;
    private bool _isVerifying;

    public AppLockVerificationDialog(IAppLockService appLockService)
    {
        _appLockService = appLockService;

        InitializeComponent();
    }

    public bool IsVerified { get; private set; }

    private async void ConfirmClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (IsVerified)
            return;

        args.Cancel = true;

        if (_isVerifying || !AppLockPinInput.CanSubmit(PinBox))
            return;

        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await VerifyPinAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void PinBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;

        if (_isVerifying || !AppLockPinInput.CanSubmit(PinBox))
            return;

        if (await VerifyPinAsync())
            Hide();
    }

    private void PinChanged(object sender, RoutedEventArgs e)
    {
        if (AppLockPinInput.KeepDigitsOnly(PinBox))
            return;

        IsPrimaryButtonEnabled = !_isVerifying && AppLockPinInput.CanSubmit(PinBox);

        // A wrong PIN clears the field; the error stays until the user types again.
        if (!string.IsNullOrEmpty(PinBox.Password))
            ErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private async Task<bool> VerifyPinAsync()
    {
        _isVerifying = true;
        IsPrimaryButtonEnabled = false;

        try
        {
            var verification = await AppLockPinInput.VerifyAsync(_appLockService, PinBox.Password);

            if (verification.IsSuccess)
            {
                IsVerified = true;
                return true;
            }

            PinBox.Password = string.Empty;
            ErrorTextBlock.Text = AppLockPinInput.GetFailureMessage(verification);
            ErrorTextBlock.Visibility = Visibility.Visible;
            PinBox.Focus(FocusState.Programmatic);
            return false;
        }
        finally
        {
            _isVerifying = false;
            IsPrimaryButtonEnabled = AppLockPinInput.CanSubmit(PinBox);
        }
    }
}
