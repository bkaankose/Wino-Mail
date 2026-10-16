using AppKit;
using AuthenticationServices;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>What the sign-in sheet ended with; the dialog service shows the follow-up sheet or alert.</summary>
internal sealed record WinoAccountLoginOutcome(
    WinoAccount? Account,
    string? PendingConfirmationEmail = null,
    EmailConfirmationRequiredDetailsDto? ConfirmationDetails = null,
    string? PasswordResetEmail = null);

/// <summary>
/// Sign in to Wino Account (port of WinoAccountLoginDialog): email and password, a forgot-password
/// mode that hides the password and sends a reset link, Return moving from Email to Password, and
/// Sign in with Apple and Sign in with Google under an "or" rule. The Apple button is Apple's own (the
/// one exception to the WinoIcons rule; HIG and App Review require it) and comes first, as Apple asks;
/// Google is a standard button with the colourful Google mark. The row hides in forgot-password mode.
/// </summary>
internal sealed class WinoAccountLoginSheet : WinoAccountSheet<WinoAccountLoginOutcome>
{
    private readonly IWinoAccountProfileService _profile;
    private readonly IWinoAccountExternalSignInService? _externalSignIn;
    private readonly NSTextField _email;
    private readonly NSTextField _password;
    private readonly NSStackView _passwordRow;
    private readonly NSTextField _title;
    private readonly NSTextField _description;
    private readonly NSButton _modeLink;
    private readonly NSStackView? _externalRow;
    private readonly List<NSControl> _externalButtons = [];
    private bool _forgot;

    public WinoAccountLoginSheet(IWinoAccountProfileService profile, IWinoAccountExternalSignInService? externalSignIn = null) : base(440)
    {
        _profile = profile;
        _externalSignIn = externalSignIn;
        AddRow(Header(Translator.WinoAccount_LoginDialog_Title, Translator.WinoAccount_LoginDialog_Description, out _title, out _description));

        var emailRow = Field(Translator.WinoAccount_EmailLabel, out _email, Translator.WinoAccount_EmailPlaceholder);
        _email.ContentType = NSTextContentType.EmailAddress;
        _passwordRow = Field(Translator.WinoAccount_PasswordLabel, out _password, secure: true);
        _password.ContentType = NSTextContentType.Password;
        _modeLink = Link(Translator.WinoAccount_LoginDialog_ForgotPasswordLink, ToggleMode);
        var fields = WinoLayout.VStack(12, emailRow, _passwordRow, _modeLink);
        fields.Alignment = NSLayoutAttribute.Leading;
        fields.SetCustomSpacing(6, _passwordRow);
        emailRow.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor).Active = true;
        _passwordRow.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor).Active = true;
        AddRow(fields);

        if (externalSignIn is { IsAppleAvailable: true })
        {
            var apple = ASAuthorizationAppleIdButton.Create(ASAuthorizationAppleIdButtonType.SignIn, ASAuthorizationAppleIdButtonStyle.Black);
            apple.TranslatesAutoresizingMaskIntoConstraints = false;
            apple.CornerRadius = 6;
            apple.HeightAnchor.ConstraintEqualTo(36).Active = true;
            WinoAccessibility.Label(apple, Translator.WinoAccount_SignInWithApple);
            apple.Activated += async (_, _) => await SignInExternallyAsync(token => externalSignIn.SignInWithAppleAsync(token));
            _externalButtons.Add(apple);
        }
        if (externalSignIn is { IsGoogleAvailable: true })
        {
            var google = new NSButton
            {
                BezelStyle = NSBezelStyle.Rounded,
                ControlSize = NSControlSize.Large,
                Title = Translator.WinoAccount_SignInWithGoogle,
                Image = WinoIcons.ColorfulImage(WinoIconGlyph.Google, 16),
                ImagePosition = NSCellImagePosition.ImageLeading,
                ImageHugsTitle = true,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            google.HeightAnchor.ConstraintEqualTo(36).Active = true;
            google.Activated += async (_, _) => await SignInExternallyAsync(token => externalSignIn.SignInWithGoogleAsync(token));
            _externalButtons.Add(google);
        }
        if (_externalButtons.Count > 0)
        {
            _externalRow = WinoLayout.VStack(10, [OrRule(), .. _externalButtons]);
            _externalRow.Alignment = NSLayoutAttribute.Leading;
            _externalRow.SetCustomSpacing(16, _externalRow.ArrangedSubviews[0]);
            foreach (var button in _externalButtons) button.WidthAnchor.ConstraintEqualTo(_externalRow.WidthAnchor).Active = true;
            AddRow(_externalRow);
        }

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
        if (_externalRow is not null) _externalRow.Hidden = _forgot;
        SetLinkTitle(_modeLink, _forgot ? Translator.WinoAccount_ForgotPasswordDialog_BackToSignIn : Translator.WinoAccount_LoginDialog_ForgotPasswordLink);
    }

    protected override void SetBusy(bool busy)
    {
        base.SetBusy(busy);
        foreach (var button in _externalButtons) button.Enabled = !busy;
    }

    /// <summary>A hairline, "or", a hairline: the rule between the password form and the Apple button.</summary>
    private static NSStackView OrRule()
    {
        var label = WinoStyle.Label(Translator.WinoAccount_LoginDialog_OrDivider, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        var leading = Hairline();
        var trailing = Hairline();
        var rule = WinoLayout.HStack(10, leading, label, trailing);
        rule.Alignment = NSLayoutAttribute.CenterY;
        rule.Distribution = NSStackViewDistribution.Fill;
        leading.WidthAnchor.ConstraintEqualTo(trailing.WidthAnchor).Active = true;
        return rule;

        static NSBox Hairline()
        {
            var line = new NSBox { BoxType = NSBoxType.NSBoxSeparator, TranslatesAutoresizingMaskIntoConstraints = false };
            line.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            return line;
        }
    }

    /// <summary>The provider's sheet, then the API; a dismissed sheet shows nothing.</summary>
    private async Task SignInExternallyAsync(Func<CancellationToken, Task<Wino.Core.Domain.Models.Accounts.WinoAccountOperationResult>> signIn)
    {
        if (IsBusy || _externalSignIn is null) return;
        SetBusy(true);
        string? error = null;
        try
        {
            var result = await signIn(CancellationToken.None);
            if (result.IsSuccess && result.Account is not null) { Finish(new WinoAccountLoginOutcome(result.Account)); return; }
            if (!WinoAccountClientErrorCodes.IsCancelled(result.ErrorCode))
                error = WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
        }
        catch (Exception exception) { error = exception.Message; }
        finally { SetBusy(false); }
        if (error is not null) await ShowErrorAsync(error);
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
                error = WinoAccountAuthErrorTranslator.Translate(response.ErrorCode);
            }
            else
            {
                var result = await _profile.LoginAsync(email, _password.StringValue);
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
        if (error is not null) await ShowErrorAsync(error);
    }
}
