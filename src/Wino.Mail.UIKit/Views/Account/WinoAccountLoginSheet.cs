using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Account;

/// <summary>What the sign-in sheet ended with; the dialog service shows the follow-up sheet or alert.</summary>
internal sealed record WinoAccountLoginOutcome(
    WinoAccount? Account,
    string? PendingConfirmationEmail = null,
    EmailConfirmationRequiredDetailsDto? ConfirmationDetails = null,
    string? PasswordResetEmail = null);

/// <summary>
/// Sign in to Wino Account (port of WinoAccountLoginDialog): email, then password, and a forgot-password mode that
/// hides the password and sends a reset link instead. Return in Email moves to Password.
/// </summary>
internal sealed class WinoAccountLoginSheet : WinoAccountSheet<WinoAccountLoginOutcome>
{
    private readonly IWinoAccountProfileService _profile;
    private readonly UITextField _email;
    private readonly UITextField _password;
    private readonly UIStackView _passwordRow;
    private readonly UILabel _description;
    private readonly UIButton _modeLink;
    private bool _forgot;

    public WinoAccountLoginSheet(IWinoAccountProfileService profile) : base(Translator.WinoAccount_LoginDialog_Title)
    {
        _profile = profile;
        AddRow(Header(Translator.WinoAccount_LoginDialog_Description, out _description));

        var emailRow = Field(Translator.WinoAccount_EmailLabel, out _email, Translator.WinoAccount_EmailPlaceholder);
        EmailField(_email);
        _email.TextContentType = UITextContentType.Username;
        _passwordRow = Field(Translator.WinoAccount_PasswordLabel, out _password, secure: true);
        _password.TextContentType = UITextContentType.Password;
        _modeLink = Link(Translator.WinoAccount_LoginDialog_ForgotPasswordLink, ToggleMode);
        var fields = WinoLayout.VStack(12, emailRow, _passwordRow, _modeLink);
        fields.Alignment = UIStackViewAlignment.Fill;
        fields.SetCustomSpacing(4, _passwordRow);
        AddRow(fields);
        UpdateMode();
    }

    private void ToggleMode()
    {
        _forgot = !_forgot;
        _password.Text = string.Empty;
        ShowError(null);
        UpdateMode();
        Refresh();
    }

    private void UpdateMode()
    {
        Title = _forgot ? Translator.WinoAccount_ForgotPasswordDialog_Title : Translator.WinoAccount_LoginDialog_Title;
        _description.Text = _forgot ? Translator.WinoAccount_ForgotPasswordDialog_Description : Translator.WinoAccount_LoginDialog_Description;
        Primary.Title = _forgot ? Translator.WinoAccount_ForgotPasswordDialog_PrimaryButton : Translator.Buttons_SignIn;
        _passwordRow.Hidden = _forgot;
        SetLinkTitle(_modeLink, _forgot ? Translator.WinoAccount_ForgotPasswordDialog_BackToSignIn : Translator.WinoAccount_LoginDialog_ForgotPasswordLink);
    }

    protected override async Task PrimaryAsync()
    {
        var email = (_email.Text ?? string.Empty).Trim();
        var password = _password.Text ?? string.Empty;
        var validation = string.IsNullOrWhiteSpace(email) ? Translator.WinoAccount_Validation_EmailRequired
            : !_forgot && string.IsNullOrWhiteSpace(password) ? Translator.WinoAccount_Validation_PasswordRequired
            : null;
        if (validation is not null) { ShowError(validation); return; }

        ShowError(null);
        SetBusy(true);
        string? error = null;
        try
        {
            if (_forgot)
            {
                var response = await _profile.ForgotPasswordAsync(email);
                if (response.IsSuccess) { Finish(new WinoAccountLoginOutcome(null, PasswordResetEmail: email)); return; }
                error = WinoAccountAuthErrorTranslator.Translate(response.ErrorCode);
            }
            else
            {
                var result = await _profile.LoginAsync(email, password);
                if (result.IsSuccess && result.Account is not null) { Finish(new WinoAccountLoginOutcome(result.Account)); return; }
                var details = WinoAccountEmailConfirmationHelper.Parse(result.ErrorDetails);
                if (WinoAccountEmailConfirmationHelper.IsEmailConfirmationRequiredError(result.ErrorCode) && details is not null)
                {
                    Finish(new WinoAccountLoginOutcome(null, email, details));
                    return;
                }
                error = WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
            }
        }
        finally { SetBusy(false); }
        ShowError(error);
    }

    protected override void Closed() => _password.Text = string.Empty;
}
