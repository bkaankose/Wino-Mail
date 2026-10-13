using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Account;

/// <summary>
/// Create Wino Account (port of WinoAccountRegistrationDialog): email, optional display name, password and
/// confirmation, then the privacy caption and link. Completes with the address the confirmation email was sent to.
/// </summary>
internal sealed class WinoAccountRegistrationSheet : WinoAccountSheet<string>
{
    private readonly IWinoAccountProfileService _profile;
    private readonly UITextField _email;
    private readonly UITextField _name;
    private readonly UITextField _password;
    private readonly UITextField _confirm;

    public WinoAccountRegistrationSheet(IWinoAccountProfileService profile) : base(Translator.WinoAccount_RegisterDialog_Title)
    {
        _profile = profile;
        AddRow(Header(Translator.WinoAccount_RegisterDialog_Description, out _));

        var email = Field(Translator.WinoAccount_EmailLabel, out _email, Translator.WinoAccount_EmailPlaceholder);
        EmailField(_email);
        _email.TextContentType = UITextContentType.Username;
        var name = Field(Translator.WinoAccount_Profile_NameOptional, out _name);
        _name.TextContentType = UITextContentType.Name;
        _name.AutocapitalizationType = UITextAutocapitalizationType.Words;
        var password = Field(Translator.WinoAccount_PasswordLabel, out _password, secure: true);
        var confirm = Field(Translator.WinoAccount_ConfirmPasswordLabel, out _confirm, secure: true);
        _password.TextContentType = UITextContentType.NewPassword;
        _confirm.TextContentType = UITextContentType.NewPassword;
        var fields = WinoLayout.VStack(12, email, name, password, confirm);
        fields.Alignment = UIStackViewAlignment.Fill;
        AddRow(fields);

        var caption = WinoStyle.Label(Translator.WinoAccount_RegisterDialog_PrivacyAgreement, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var link = Link(Translator.WinoAccount_RegisterDialog_PrivacyLinkText,
            () => UIApplication.SharedApplication.OpenUrl(new NSUrl(AppUrls.PrivacyPolicy), new UIApplicationOpenUrlOptions(), null));
        var privacy = WinoLayout.VStack(2, caption, link);
        privacy.Alignment = UIStackViewAlignment.Fill;
        AddRow(privacy);

        Primary.Title = Translator.WinoAccount_RegisterDialog_PrimaryButton;
    }

    protected override async Task PrimaryAsync()
    {
        var email = (_email.Text ?? string.Empty).Trim();
        var password = _password.Text ?? string.Empty;
        var validation = string.IsNullOrWhiteSpace(email) ? Translator.WinoAccount_Validation_EmailRequired
            : string.IsNullOrWhiteSpace(password) ? Translator.WinoAccount_Validation_PasswordRequired
            : !string.Equals(password, _confirm.Text ?? string.Empty, StringComparison.Ordinal) ? Translator.WinoAccount_Validation_PasswordMismatch
            : null;
        if (validation is not null) { ShowError(validation); return; }

        ShowError(null);
        SetBusy(true);
        string? error = null;
        try
        {
            var result = await _profile.RegisterWithProfileAsync(email, password, (_name.Text ?? string.Empty).Trim());
            if (result.IsSuccess && result.Account is not null) { Finish(result.Account.Email); return; }
            error = WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
        }
        finally { SetBusy(false); }
        ShowError(error);
    }

    protected override void Closed()
    {
        _password.Text = string.Empty;
        _confirm.Text = string.Empty;
    }
}
