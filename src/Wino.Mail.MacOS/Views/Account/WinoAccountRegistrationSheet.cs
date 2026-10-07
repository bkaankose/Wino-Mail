using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Account;

/// <summary>
/// Create Wino Account (port of WinoAccountRegistrationDialog): email, optional display name,
/// password and confirmation, the privacy caption and link. Completes with the address the
/// confirmation email was sent to.
/// </summary>
internal sealed class WinoAccountRegistrationSheet : WinoAccountSheet<string>
{
    private readonly IWinoAccountProfileService _profile;
    private readonly NSTextField _email;
    private readonly NSTextField _name;
    private readonly NSTextField _password;
    private readonly NSTextField _confirm;

    public WinoAccountRegistrationSheet(IWinoAccountProfileService profile) : base(440)
    {
        _profile = profile;
        Sheet.Title = Translator.WinoAccount_RegisterDialog_Title;
        AddRow(Header(Translator.WinoAccount_RegisterDialog_Title, Translator.WinoAccount_RegisterDialog_Description, out _, out _));

        var email = Field(Translator.WinoAccount_EmailLabel, out _email, Translator.WinoAccount_EmailPlaceholder);
        _email.ContentType = NSTextContentType.EmailAddress;
        var name = Field(Translator.WinoAccount_Profile_NameOptional, out _name);
        var password = Field(Translator.WinoAccount_PasswordLabel, out _password, secure: true);
        var confirm = Field(Translator.WinoAccount_ConfirmPasswordLabel, out _confirm, secure: true);
        _password.ContentType = NSTextContentType.NewPassword;
        _confirm.ContentType = NSTextContentType.NewPassword;
        var fields = WinoLayout.VStack(12, email, name, password, confirm);
        fields.Alignment = NSLayoutAttribute.Leading;
        foreach (var row in new NSView[] { email, name, password, confirm }) row.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor).Active = true;
        AddRow(fields);

        var caption = WinoStyle.Label(Translator.WinoAccount_RegisterDialog_PrivacyAgreement, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        var link = Link(Translator.WinoAccount_RegisterDialog_PrivacyLinkText, () => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(AppUrls.PrivacyPolicy)));
        var privacy = WinoLayout.VStack(4, caption, link);
        privacy.Alignment = NSLayoutAttribute.Leading;
        caption.WidthAnchor.ConstraintEqualTo(privacy.WidthAnchor).Active = true;
        AddRow(privacy);

        Secondary.Title = Translator.Buttons_Cancel;
        Primary.Title = Translator.WinoAccount_RegisterDialog_PrimaryButton;
#if DEBUG
        WinoAccountSheetsDebug.Prepare(this, _email, _name);
#endif
    }

    protected override NSView? InitialResponder => _email;
    protected override IEnumerable<NSTextField> Fields => [_email, _name, _password, _confirm];

    protected override async Task PrimaryAsync()
    {
        // Windows: Return walks Email, Password, Confirm; Return in Confirm (or the button) registers.
        if (IsEditing(_email) && string.IsNullOrEmpty(_password.StringValue)) { Sheet.MakeFirstResponder(_password); return; }
        if (IsEditing(_password) && string.IsNullOrEmpty(_confirm.StringValue)) { Sheet.MakeFirstResponder(_confirm); return; }

        var validation = string.IsNullOrWhiteSpace(_email.StringValue) ? Translator.WinoAccount_Validation_EmailRequired
            : string.IsNullOrWhiteSpace(_password.StringValue) ? Translator.WinoAccount_Validation_PasswordRequired
            : !string.Equals(_password.StringValue, _confirm.StringValue, StringComparison.Ordinal) ? Translator.WinoAccount_Validation_PasswordMismatch
            : null;
        if (validation is not null) { await ShowErrorAsync(validation); return; }

        SetBusy(true);
        string? error = null;
        try
        {
            var result = await _profile.RegisterWithProfileAsync(_email.StringValue.Trim(), _password.StringValue, _name.StringValue.Trim());
            if (result.IsSuccess && result.Account is not null) { Finish(result.Account.Email); return; }
            error = WinoAccountErrorText.Format(result.ErrorCode, result.ErrorMessage);
        }
        finally { SetBusy(false); }
        await ShowErrorAsync(error);
    }
}
