using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Account;

/// <summary>
/// Email confirmation required (port of WinoAccountEmailConfirmationRequiredDialog): the pending address in a subtle
/// card with a one-second resend countdown; Resend stays disabled until it ends. Completes with true when a new
/// confirmation email was sent.
/// </summary>
internal sealed class WinoAccountEmailConfirmationSheet : WinoAccountSheet<bool>
{
    private readonly IWinoAccountProfileService _profile;
    private readonly string _endpoint;
    private readonly string _ticket;
    private readonly DateTimeOffset _availableAt;
    private readonly UILabel _countdown;
    private NSTimer? _timer;

    public WinoAccountEmailConfirmationSheet(IWinoAccountProfileService profile, string email, EmailConfirmationRequiredDetailsDto details)
        : base(Translator.WinoAccount_EmailConfirmationPendingDialog_Title)
    {
        _profile = profile;
        _endpoint = details.ResendConfirmationEndpoint;
        _ticket = details.ResendConfirmationTicket;
        _availableAt = details.ResendAvailableAtUtc;
        Body.Spacing = 16;

        var message = WinoStyle.Label(string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Message, email), WinoStyle.Body, WinoStyle.PrimaryText, 0);
        _countdown = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        var text = WinoLayout.Insets(WinoLayout.VStack(8, message, _countdown), 14, 14, 14, 14);
        text.Alignment = UIStackViewAlignment.Fill;
        var card = new WinoSurfaceView { Fill = WinoStyle.SubtleFill, CornerRadius = 12, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(text, card);
        AddRow(card);

        Secondary.Title = Translator.Buttons_Close;
        Primary.Title = Translator.WinoAccount_EmailConfirmationPendingDialog_ResendButton;
        UpdateCountdown();
    }

    protected override bool CanSubmit => DateTimeOffset.UtcNow >= _availableAt;

    protected override void Opened()
        => _timer = NSTimer.CreateRepeatingScheduledTimer(1, _ => UpdateCountdown());

    protected override void Closed()
    {
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = null;
    }

    private void UpdateCountdown()
    {
        var remaining = _availableAt - DateTimeOffset.UtcNow;
        Refresh();
        _countdown.Text = remaining <= TimeSpan.Zero
            ? Translator.WinoAccount_EmailConfirmationPendingDialog_ReadyToResend
            : string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Countdown, $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}");
    }

    protected override async Task PrimaryAsync()
    {
        if (!CanSubmit) { UpdateCountdown(); return; }
        ShowError(null);
        SetBusy(true);
        string? error = null;
        try
        {
            var response = await _profile.ResendEmailConfirmationAsync(_endpoint, _ticket);
            if (response.IsSuccess) { Finish(true); return; }
            error = WinoAccountAuthErrorTranslator.Translate(response.ErrorCode);
        }
        finally { SetBusy(false); }
        ShowError(error);
    }
}
