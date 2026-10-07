using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Email confirmation required (port of WinoAccountEmailConfirmationRequiredDialog): the pending
/// address in a subtle card with a one-second resend countdown; Resend stays disabled until it ends.
/// Completes with true when a new confirmation email was sent.
/// </summary>
internal sealed class WinoAccountEmailConfirmationSheet : WinoAccountSheet<bool>
{
    private readonly IWinoAccountProfileService _profile;
    private readonly string _endpoint;
    private readonly string _ticket;
    private readonly DateTimeOffset _availableAt;
    private readonly NSTextField _countdown;
    private NSTimer? _timer;

    public WinoAccountEmailConfirmationSheet(IWinoAccountProfileService profile, string email, EmailConfirmationRequiredDetailsDto details) : base(520)
    {
        _profile = profile;
        _endpoint = details.ResendConfirmationEndpoint;
        _ticket = details.ResendConfirmationTicket;
        _availableAt = details.ResendAvailableAtUtc;
        Body.Spacing = 16;
        Sheet.Title = Translator.WinoAccount_EmailConfirmationPendingDialog_Title;

        AddRow(WinoStyle.Label(Translator.WinoAccount_EmailConfirmationPendingDialog_Title, WinoStyle.Heading, WinoStyle.PrimaryText, 0));
        var message = WinoStyle.Label(string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Message, email), WinoStyle.Body, WinoStyle.PrimaryText, 0);
        _countdown = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText, 0);
        var text = WinoLayout.VStack(8, message, _countdown);
        text.Alignment = NSLayoutAttribute.Leading;
        text.EdgeInsets = new NSEdgeInsets(14, 14, 14, 14);
        message.WidthAnchor.ConstraintEqualTo(text.WidthAnchor, 1, -28).Active = true;
        _countdown.WidthAnchor.ConstraintEqualTo(text.WidthAnchor, 1, -28).Active = true;
        var card = new WinoSurfaceView { Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.045), WinoStyle.Hex(0xFFFFFF, 0.06)), CornerRadius = 12 };
        WinoLayout.Fill(text, card);
        AddRow(card);

        // The busy ring sits at the leading edge on this dialog.
        SpinnerAtLeadingEdge = true;
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
        if (!IsBusy) Primary.Enabled = remaining <= TimeSpan.Zero;
        _countdown.StringValue = remaining <= TimeSpan.Zero
            ? Translator.WinoAccount_EmailConfirmationPendingDialog_ReadyToResend
            : string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Countdown, $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}");
    }

    protected override async Task PrimaryAsync()
    {
        if (!CanSubmit) { UpdateCountdown(); return; }
        SetBusy(true);
        string? error = null;
        try
        {
            var response = await _profile.ResendEmailConfirmationAsync(_endpoint, _ticket);
            if (response.IsSuccess) { Finish(true); return; }
            error = WinoAccountErrorText.Translate(response.ErrorCode);
        }
        finally { SetBusy(false); }
        await ShowErrorAsync(error);
    }
}
