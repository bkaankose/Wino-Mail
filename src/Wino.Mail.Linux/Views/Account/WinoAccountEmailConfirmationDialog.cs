using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Account;

/// <summary>
/// E-mail confirmation required (Windows WinoAccountEmailConfirmationRequiredDialog, macOS WinoAccountEmailConfirmationSheet):
/// the pending address in a card with a one-second resend countdown; Resend stays insensitive until it ends.
/// Completes with true when a new confirmation mail was sent.
/// </summary>
internal sealed class WinoAccountEmailConfirmationDialog
{
    private readonly IWinoAccountProfileService _profile;
    private readonly string _endpoint;
    private readonly string _ticket;
    private readonly DateTimeOffset _availableAt;
    private readonly FormDialog _form = new(Translator.WinoAccount_EmailConfirmationPendingDialog_Title, null,
        Translator.WinoAccount_EmailConfirmationPendingDialog_ResendButton, cancelText: Translator.Buttons_Close);
    private readonly Gtk.Label _countdown = Gtk.Label.New(string.Empty);
    private uint _timer;

    public WinoAccountEmailConfirmationDialog(IWinoAccountProfileService profile, string email, EmailConfirmationRequiredDetailsDto details)
    {
        _profile = profile;
        _endpoint = details.ResendConfirmationEndpoint;
        _ticket = details.ResendConfirmationTicket;
        _availableAt = details.ResendAvailableAtUtc;
        _form.ContentWidth = 480;

        var message = Gtk.Label.New(string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Message, email));
        message.SetWrap(true);
        message.SetXalign(0);
        _countdown.SetWrap(true);
        _countdown.SetXalign(0);
        _countdown.AddCssClass("dim-label");
        var card = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        card.AddCssClass("card");
        foreach (var label in (Gtk.Label[])[message, _countdown])
        {
            label.SetMarginStart(14);
            label.SetMarginEnd(14);
            card.Append(label);
        }
        message.SetMarginTop(14);
        _countdown.SetMarginBottom(14);
        _form.Add(card);

        _form.CanConfirm = () => DateTimeOffset.UtcNow >= _availableAt;
        _form.ValidateAsync = ResendAsync;
        _form.Own(new ActionDisposable(StopTimer));
        UpdateCountdown();
        _timer = GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 1000, () =>
        {
            UpdateCountdown();
            if (DateTimeOffset.UtcNow < _availableAt) return true;
            _timer = 0;
            return false;
        });
        WinoLiveObjects.Track(this);
    }

    /// <summary>Main thread; true when a new confirmation mail was sent.</summary>
    public async Task<bool> PresentAsync(Gtk.Widget owner)
    {
#if DEBUG
        Current = this;
#endif
        try { return await _form.PresentAsync(owner); }
        finally
        {
#if DEBUG
            if (ReferenceEquals(Current, this)) Current = null;
#endif
        }
    }

    private void UpdateCountdown()
    {
        var remaining = _availableAt - DateTimeOffset.UtcNow;
        _countdown.SetText(remaining <= TimeSpan.Zero
            ? Translator.WinoAccount_EmailConfirmationPendingDialog_ReadyToResend
            : string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Countdown, $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}"));
        _form.Refresh();
    }

    private void StopTimer()
    {
        if (_timer == 0) return;
        GLib.Functions.SourceRemove(_timer);
        _timer = 0;
    }

    private async Task<string?> ResendAsync()
    {
        var response = await _profile.ResendEmailConfirmationAsync(_endpoint, _ticket);
        return response.IsSuccess ? null : WinoAccountAuthErrorTranslator.Translate(response.ErrorCode);
    }

#if DEBUG
    internal static WinoAccountEmailConfirmationDialog? Current { get; private set; }

    internal string Describe() => $"countdown='{_countdown.GetText()}' canResend={DateTimeOffset.UtcNow >= _availableAt} timer={_timer != 0} error='{_form.ErrorText}'";

    internal void DebugClose() => _form.Dialog.Close();
#endif
}
