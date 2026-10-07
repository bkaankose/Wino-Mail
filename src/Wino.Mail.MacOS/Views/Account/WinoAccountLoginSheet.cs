using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>What the sign-in sheet ended with; the dialog service shows the follow-up sheet or alert.</summary>
internal sealed record WinoAccountLoginOutcome(
    WinoAccount? Account,
    string? PendingConfirmationEmail = null,
    EmailConfirmationRequiredDetailsDto? ConfirmationDetails = null,
    string? PasswordResetEmail = null);

/// <summary>
/// Sign in to Wino Account (port of WinoAccountLoginDialog): email and password, a forgot-password
/// mode that hides the password and sends a reset link, Return moving from Email to Password.
/// </summary>
internal sealed class WinoAccountLoginSheet : WinoAccountSheet<WinoAccountLoginOutcome>
{
    private readonly IWinoAccountProfileService _profile;
    private readonly NSTextField _email;
    private readonly NSTextField _password;
    private readonly NSStackView _passwordRow;
    private readonly NSTextField _title;
    private readonly NSTextField _description;
    private readonly NSButton _modeLink;
    private bool _forgot;

    public WinoAccountLoginSheet(IWinoAccountProfileService profile) : base(440)
    {
        _profile = profile;
        AddRow(Header(Translator.WinoAccount_LoginDialog_Title, Translator.WinoAccount_LoginDialog_Description, out _title, out _description));

        var emailRow = Field(Translator.WinoAccount_EmailLabel, out _email, Translator.WinoAccount_EmailPlaceholder);
        _email.ContentType = NSTextContentType.EmailAddress;
        _passwordRow = Field(Translator.WinoAccount_PasswordLabel, out _password, secure: true);
        _password.ContentType = NSTextContentType.Password;
        _modeLink = Link(Translator.WinoAccount_LoginDialog_ForgotPasswordLink, ToggleMode);
        var fields = Wino.Presentation.AppKit.WinoLayout.VStack(12, emailRow, _passwordRow, _modeLink);
        fields.Alignment = NSLayoutAttribute.Leading;
        fields.SetCustomSpacing(6, _passwordRow);
        emailRow.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor).Active = true;
        _passwordRow.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor).Active = true;
        AddRow(fields);

        Secondary.Title = Translator.Buttons_Cancel;
        UpdateMode();
#if DEBUG
        WinoAccountSheetsDebug.Prepare(this, _email);
#endif
    }

    protected override NSView? InitialResponder => _email;
    protected override IEnumerable<NSTextField> Fields => [_email, _password];

    private void ToggleMode()
    {
        _forgot = !_forgot;
        _password.StringValue = string.Empty;
        UpdateMode();
        Resize();
    }

    private void UpdateMode()
    {
        var title = _forgot ? Translator.WinoAccount_ForgotPasswordDialog_Title : Translator.WinoAccount_LoginDialog_Title;
        _title.StringValue = title;
        Sheet.Title = title;
        _description.StringValue = _forgot ? Translator.WinoAccount_ForgotPasswordDialog_Description : Translator.WinoAccount_LoginDialog_Description;
        Primary.Title = _forgot ? Translator.WinoAccount_ForgotPasswordDialog_PrimaryButton : Translator.Buttons_SignIn;
        _passwordRow.Hidden = _forgot;
        SetLinkTitle(_modeLink, _forgot ? Translator.WinoAccount_ForgotPasswordDialog_BackToSignIn : Translator.WinoAccount_LoginDialog_ForgotPasswordLink);
    }

#if DEBUG
    internal override void ShowDebugState(string state)
    {
        if (state == "forgot") ToggleMode();
        else base.ShowDebugState(state);
    }
#endif

    protected override async Task PrimaryAsync()
    {
        // Windows: Return in Email moves to Password; only Return in Password (or the button) submits.
        if (!_forgot && IsEditing(_email) && string.IsNullOrEmpty(_password.StringValue))
        {
            Sheet.MakeFirstResponder(_password);
            return;
        }

        var email = _email.StringValue.Trim();
        var validation = string.IsNullOrWhiteSpace(email) ? Translator.WinoAccount_Validation_EmailRequired
            : !_forgot && string.IsNullOrWhiteSpace(_password.StringValue) ? Translator.WinoAccount_Validation_PasswordRequired
            : null;
        if (validation is not null) { await ShowErrorAsync(validation); return; }

        SetBusy(true);
        string? error = null;
        try
        {
            if (_forgot)
            {
                var response = await _profile.ForgotPasswordAsync(email);
                if (response.IsSuccess) { Finish(new WinoAccountLoginOutcome(null, PasswordResetEmail: email)); return; }
                error = WinoAccountErrorText.Translate(response.ErrorCode);
            }
            else
            {
                var result = await _profile.LoginAsync(email, _password.StringValue);
                if (result.IsSuccess && result.Account is not null) { Finish(new WinoAccountLoginOutcome(result.Account)); return; }
                var details = WinoAccountErrorText.ParseConfirmation(result.ErrorDetails);
                if (WinoAccountErrorText.IsEmailConfirmationRequired(result.ErrorCode) && details is not null)
                {
                    Finish(new WinoAccountLoginOutcome(null, email, details));
                    return;
                }
                error = WinoAccountErrorText.Format(result.ErrorCode, result.ErrorMessage);
            }
        }
        finally { SetBusy(false); }
        if (error is not null) await ShowErrorAsync(error);
    }
}
