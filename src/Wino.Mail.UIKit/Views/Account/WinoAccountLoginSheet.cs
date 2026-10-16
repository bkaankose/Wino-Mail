using AuthenticationServices;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
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
/// hides the password and sends a reset link instead. Return in Email moves to Password. Sign in with Apple and
/// Sign in with Google sit under an "or" rule: Apple's own button first (the one exception to the WinoIcons rule;
/// HIG and App Review require it), then a plain button with the colourful Google mark. The row hides in
/// forgot-password mode.
/// </summary>
internal sealed class WinoAccountLoginSheet : WinoAccountSheet<WinoAccountLoginOutcome>
{
    private readonly IWinoAccountProfileService _profile;
    private readonly IWinoAccountExternalSignInService? _externalSignIn;
    private readonly UITextField _email;
    private readonly UITextField _password;
    private readonly UIStackView _passwordRow;
    private readonly UILabel _description;
    private readonly UIButton _modeLink;
    private readonly UIStackView? _externalRow;
    private bool _forgot;

    public WinoAccountLoginSheet(IWinoAccountProfileService profile, IWinoAccountExternalSignInService? externalSignIn = null) : base(Translator.WinoAccount_LoginDialog_Title)
    {
        _profile = profile;
        _externalSignIn = externalSignIn;
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

        var buttons = new List<UIView>();
        if (externalSignIn is { IsAppleAvailable: true })
        {
            // Apple's button does not follow appearance changes; the sheet is short-lived, so it is built for the current one.
            var dark = UITraitCollection.CurrentTraitCollection.UserInterfaceStyle == UIUserInterfaceStyle.Dark;
            var apple = ASAuthorizationAppleIdButton.Create(ASAuthorizationAppleIdButtonType.SignIn, dark ? ASAuthorizationAppleIdButtonStyle.White : ASAuthorizationAppleIdButtonStyle.Black);
            apple.TranslatesAutoresizingMaskIntoConstraints = false;
            apple.CornerRadius = 10;
            apple.HeightAnchor.ConstraintEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
            apple.AccessibilityLabel = Translator.WinoAccount_SignInWithApple;
            apple.PrimaryActionTriggered += async (_, _) => await SignInExternallyAsync(token => externalSignIn.SignInWithAppleAsync(token));
            buttons.Add(apple);
        }
        if (externalSignIn is { IsGoogleAvailable: true })
        {
            var google = GoogleButton();
            google.PrimaryActionTriggered += async (_, _) => await SignInExternallyAsync(token => externalSignIn.SignInWithGoogleAsync(token));
            buttons.Add(google);
        }
        if (buttons.Count > 0)
        {
            _externalRow = WinoLayout.VStack(10, [OrRule(), .. buttons]);
            _externalRow.Alignment = UIStackViewAlignment.Fill;
            _externalRow.SetCustomSpacing(16, _externalRow.ArrangedSubviews[0]);
            AddRow(_externalRow);
        }
        UpdateMode();
    }

    /// <summary>Google's sign-in button drawing: the colourful mark and the title on a neutral, outlined pill.</summary>
    private static UIButton GoogleButton()
    {
        var configuration = UIButtonConfiguration.FilledButtonConfiguration;
        configuration.Title = Translator.WinoAccount_SignInWithGoogle;
        configuration.Image = WinoIcons.ColorfulImage(WinoIconGlyph.Google, 18);
        configuration.ImagePadding = 8;
        configuration.BaseForegroundColor = WinoStyle.PrimaryText;
        configuration.BaseBackgroundColor = UIColor.SystemBackground;
        configuration.Background.StrokeColor = WinoStyle.Separator;
        configuration.Background.StrokeWidth = 1;
        configuration.Background.CornerRadius = 10;
        configuration.CornerStyle = UIButtonConfigurationCornerStyle.Fixed;
        var button = new UIButton(UIButtonType.System) { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        button.HeightAnchor.ConstraintEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        button.AccessibilityLabel = Translator.WinoAccount_SignInWithGoogle;
        return button;
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
        if (_externalRow is not null) _externalRow.Hidden = _forgot;
        SetLinkTitle(_modeLink, _forgot ? Translator.WinoAccount_ForgotPasswordDialog_BackToSignIn : Translator.WinoAccount_LoginDialog_ForgotPasswordLink);
    }

    /// <summary>A hairline, "or", a hairline: the rule between the password form and the Apple button.</summary>
    private static UIStackView OrRule()
    {
        var label = WinoStyle.Label(Translator.WinoAccount_LoginDialog_OrDivider, WinoStyle.Description, WinoStyle.SecondaryText);
        label.SetContentHuggingPriority((float)UILayoutPriority.Required, UILayoutConstraintAxis.Horizontal);
        var leading = Hairline();
        var trailing = Hairline();
        var rule = WinoLayout.HStack(10, leading, label, trailing);
        rule.Alignment = UIStackViewAlignment.Center;
        leading.WidthAnchor.ConstraintEqualTo(trailing.WidthAnchor).Active = true;
        return rule;

        static UIView Hairline()
        {
            var line = new UIView { BackgroundColor = WinoStyle.Separator, TranslatesAutoresizingMaskIntoConstraints = false };
            line.HeightAnchor.ConstraintEqualTo((nfloat)(1 / UIScreen.MainScreen.Scale)).Active = true;
            return line;
        }
    }

    /// <summary>The provider's sheet, then the API; a dismissed sheet shows nothing. The base busy state covers the body, the provider buttons included.</summary>
    private async Task SignInExternallyAsync(Func<CancellationToken, Task<Wino.Core.Domain.Models.Accounts.WinoAccountOperationResult>> signIn)
    {
        if (IsBusy || _externalSignIn is null) return;
        ShowError(null);
        SetBusy(true);
        string? error = null;
        try
        {
            var result = await signIn(CancellationToken.None);
            if (result.IsSuccess && result.Account is not null) { Finish(new WinoAccountLoginOutcome(result.Account)); return; }
            if (!WinoAccountClientErrorCodes.IsCancelled(result.ErrorCode))
                error = WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "External sign-in failed.");
            error = exception.Message;
        }
        finally { SetBusy(false); }
        ShowError(error);
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
