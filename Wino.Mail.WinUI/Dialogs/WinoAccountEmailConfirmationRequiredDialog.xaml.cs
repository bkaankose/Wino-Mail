using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.WinUI.Services;

namespace Wino.Dialogs;

public sealed partial class WinoAccountEmailConfirmationRequiredDialog : ContentDialog
{
    private readonly IWinoAccountProfileService _profileService;
    private readonly DispatcherTimer _countdownTimer;
    private readonly string _email;
    private readonly string _endpoint;
    private readonly string _ticket;
    private DateTimeOffset _resendAvailableAtUtc;

    public WinoAccountEmailConfirmationRequiredDialog(IWinoAccountProfileService profileService, string email, EmailConfirmationRequiredDetailsDto details)
    {
        _profileService = profileService;
        _email = email;
        _endpoint = details.ResendConfirmationEndpoint;
        _ticket = details.ResendConfirmationTicket;
        _resendAvailableAtUtc = details.ResendAvailableAtUtc;

        InitializeComponent();

        MessageTextBlock.Text = string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Message, email);

        _countdownTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdownTimer.Tick += CountdownTimer_Tick;

        Closing += DialogClosing;

        UpdateCountdown();
        _countdownTimer.Start();
    }

    public bool ResendSucceeded { get; private set; }

    private async void ResendClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;

        if (DateTimeOffset.UtcNow < _resendAvailableAtUtc)
        {
            UpdateCountdown();
            return;
        }

        var deferral = args.GetDeferral();

        try
        {
            SetBusyState(true);
            HideError();

            var response = await _profileService.ResendEmailConfirmationAsync(_endpoint, _ticket);
            if (!response.IsSuccess)
            {
                ShowError(WinoAccountAuthErrorTranslator.Translate(response.ErrorCode));
                return;
            }

            ResendSucceeded = true;
            Hide();
        }
        finally
        {
            SetBusyState(false);
            deferral.Complete();
        }
    }

    private void CountdownTimer_Tick(object? sender, object e) => UpdateCountdown();

    private void UpdateCountdown()
    {
        var remaining = _resendAvailableAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            IsPrimaryButtonEnabled = true;
            CountdownTextBlock.Text = Translator.WinoAccount_EmailConfirmationPendingDialog_ReadyToResend;
            return;
        }

        IsPrimaryButtonEnabled = false;
        CountdownTextBlock.Text = string.Format(
            Translator.WinoAccount_EmailConfirmationPendingDialog_Countdown,
            $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}");
    }

    private void SetBusyState(bool isBusy)
    {
        IsPrimaryButtonEnabled = !isBusy && DateTimeOffset.UtcNow >= _resendAvailableAtUtc;
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

    private void DialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _countdownTimer.Stop();
        _countdownTimer.Tick -= CountdownTimer_Tick;
        Closing -= DialogClosing;
    }
}
