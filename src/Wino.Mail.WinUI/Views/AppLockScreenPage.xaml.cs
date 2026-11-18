#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.AppLock;
using Wino.Mail.WinUI.Helpers;

namespace Wino.Mail.WinUI.Views;

/// <summary>
/// The shell window's content while the app lock is on. Unlocking only tells the lock service; the window
/// watches the service and replaces this page with the content it was asked to show.
/// </summary>
public sealed partial class AppLockScreenPage : Page
{
    private readonly IAppLockService _appLockService = WinoApplication.Current.Services.GetRequiredService<IAppLockService>();
    private bool _isLoadedOnce;
    private bool _isVerifying;
    private DateTimeOffset _retryNotBefore = DateTimeOffset.MinValue;

    public AppLockScreenPage()
    {
        InitializeComponent();
    }

    private void PageLoaded(object sender, RoutedEventArgs e)
    {
        if (_isLoadedOnce)
            return;

        _isLoadedOnce = true;

        if (!_appLockService.IsBiometricEnabled)
        {
            DescriptionTextBlock.Text = Translator.AppLock_LockedDescription;
            PinPanel.Visibility = Visibility.Visible;
            PinBox.Focus(FocusState.Programmatic);
            return;
        }

        DescriptionTextBlock.Text = Translator.AppLock_LockedHelloDescription;
        HelloButton.Visibility = Visibility.Visible;
        HelloButton.Focus(FocusState.Programmatic);

        // The prompt is owned by the active window, which becomes this one only after its first frame.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => await VerifyWithHelloAsync());
    }

    private void PinChanged(object sender, RoutedEventArgs e)
    {
        if (AppLockPinInput.KeepDigitsOnly(PinBox))
            return;

        RefreshUnlockButton();

        // A wrong PIN clears the field; the error stays until the user types again.
        if (!string.IsNullOrEmpty(PinBox.Password) && !IsThrottled)
            ErrorTextBlock.Visibility = Visibility.Collapsed;
    }

    private async void PinBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        await VerifyPinAsync();
    }

    private async void UnlockClicked(object sender, RoutedEventArgs e) => await VerifyPinAsync();

    private async void HelloClicked(object sender, RoutedEventArgs e) => await VerifyWithHelloAsync();

    private bool IsThrottled => DateTimeOffset.UtcNow < _retryNotBefore;

    private void RefreshUnlockButton()
        => UnlockButton.IsEnabled = !_isVerifying && !IsThrottled && AppLockPinInput.CanSubmit(PinBox);

    private async Task VerifyPinAsync()
    {
        if (_isVerifying || IsThrottled || !AppLockPinInput.CanSubmit(PinBox))
            return;

        _isVerifying = true;
        RefreshUnlockButton();

        try
        {
            var verification = await AppLockPinInput.VerifyAsync(_appLockService, PinBox.Password);

            if (verification.IsSuccess)
            {
                _appLockService.UnlockSession();
                return;
            }

            if (verification.Status == AppLockPinVerificationStatus.Throttled)
            {
                _retryNotBefore = DateTimeOffset.UtcNow + verification.RetryAfter;
                _ = EnableUnlockAfterThrottleAsync(verification.RetryAfter);
            }

            PinBox.Password = string.Empty;
            ErrorTextBlock.Text = AppLockPinInput.GetFailureMessage(verification);
            ErrorTextBlock.Visibility = Visibility.Visible;
            PinBox.Focus(FocusState.Programmatic);
        }
        finally
        {
            _isVerifying = false;
            RefreshUnlockButton();
        }
    }

    private async Task EnableUnlockAfterThrottleAsync(TimeSpan retryAfter)
    {
        await Task.Delay(retryAfter);

        if (IsThrottled)
            return;

        ErrorTextBlock.Visibility = Visibility.Collapsed;
        RefreshUnlockButton();
    }

    private async Task VerifyWithHelloAsync()
    {
        if (_isVerifying)
            return;

        _isVerifying = true;
        HelloButton.IsEnabled = false;

        try
        {
            // Checked on every attempt: the user can set Windows Hello up again while this page is showing.
            var isAvailable = await _appLockService.IsBiometricAvailableAsync();
            ErrorTextBlock.Text = Translator.AppLock_LockedHelloUnavailable;
            ErrorTextBlock.Visibility = isAvailable ? Visibility.Collapsed : Visibility.Visible;
            SignInOptionsButton.Visibility = ErrorTextBlock.Visibility;

            if (isAvailable && await _appLockService.VerifyBiometricAsync())
                _appLockService.UnlockSession();
        }
        finally
        {
            _isVerifying = false;
            HelloButton.IsEnabled = true;
        }
    }
}
