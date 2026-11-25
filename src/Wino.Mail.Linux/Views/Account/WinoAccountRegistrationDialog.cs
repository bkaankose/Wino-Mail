using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Account;

/// <summary>
/// Create a Wino Account (Windows WinoAccountRegistrationDialog, macOS WinoAccountRegistrationSheet): e-mail, optional
/// name, password and confirmation, the privacy line and link. Completes with the address the confirmation mail went to.
/// </summary>
internal sealed class WinoAccountRegistrationDialog
{
    private readonly IWinoAccountProfileService _profile;
    private readonly FormDialog _form = new(Translator.WinoAccount_RegisterDialog_Title, Translator.WinoAccount_RegisterDialog_Description, Translator.WinoAccount_RegisterDialog_PrimaryButton);
    private readonly Adw.EntryRow _email = WinoSettingsRows.Entry(Translator.WinoAccount_EmailLabel);
    private readonly Adw.EntryRow _name = WinoSettingsRows.Entry(Translator.WinoAccount_Profile_NameOptional);
    private readonly Adw.PasswordEntryRow _password = WinoSettingsRows.Password(Translator.WinoAccount_PasswordLabel);
    private readonly Adw.PasswordEntryRow _confirm = WinoSettingsRows.Password(Translator.WinoAccount_ConfirmPasswordLabel);
    private string? _registered;

    public WinoAccountRegistrationDialog(IWinoAccountProfileService profile, IExternalLauncher launcher)
    {
        _profile = profile;
        _form.ContentWidth = 440;
        _email.SetInputPurpose(Gtk.InputPurpose.Email);
        var group = _form.AddGroup();
        foreach (var row in (Adw.EntryRow[])[_email, _name, _password, _confirm])
        {
            group.Add(row);
            row.SetActivatesDefault(false);
        }
        _confirm.SetActivatesDefault(true);

        var caption = Gtk.Label.New(Translator.WinoAccount_RegisterDialog_PrivacyAgreement);
        caption.SetWrap(true);
        caption.SetXalign(0);
        caption.AddCssClass("caption");
        caption.AddCssClass("dim-label");
        var link = Gtk.Button.NewWithLabel(Translator.WinoAccount_RegisterDialog_PrivacyLinkText);
        link.AddCssClass("link");
        link.SetHalign(Gtk.Align.Start);
        WinoA11y.Rename(link, Translator.WinoAccount_RegisterDialog_PrivacyLinkText);
        _form.Add(caption);
        _form.Add(link);
        _form.Own(GtkSignals.OnClicked(link, () => _ = launcher.LaunchUriAsync(new Uri(AppUrls.PrivacyPolicy))));

        // Windows: Return walks e-mail, name, password and confirmation; Return in the confirmation registers.
        Next(_email, _name);
        Next(_name, _password);
        Next(_password, _confirm);
        foreach (var row in (Adw.EntryRow[])[_email, _name, _password, _confirm]) _form.Own(GtkSignals.OnChanged(row, () => _form.ShowError(null)));

        _form.Validate = () =>
            string.IsNullOrWhiteSpace(_email.GetText()) ? Translator.WinoAccount_Validation_EmailRequired
            : string.IsNullOrWhiteSpace(_password.GetText()) ? Translator.WinoAccount_Validation_PasswordRequired
            : !string.Equals(_password.GetText(), _confirm.GetText(), StringComparison.Ordinal) ? Translator.WinoAccount_Validation_PasswordMismatch
            : null;
        _form.ValidateAsync = RegisterAsync;
        _form.Own(new ActionDisposable(() =>
        {
            _password.SetText(string.Empty);
            _confirm.SetText(string.Empty);
        }));
        _form.SetInitialFocus(_email);
        WinoLiveObjects.Track(this);
    }

    /// <summary>Main thread; the registered address, or null when the user cancelled.</summary>
    public async Task<string?> PresentAsync(Gtk.Widget owner)
    {
#if DEBUG
        Current = this;
#endif
        try { return await _form.PresentAsync(owner) ? _registered : null; }
        finally
        {
#if DEBUG
            if (ReferenceEquals(Current, this)) Current = null;
#endif
        }
    }

    private void Next(Adw.EntryRow from, Adw.EntryRow to)
        => _form.Own(GtkSignals.Connect<Adw.EntryRow>(h => from.OnEntryActivated += h, h => from.OnEntryActivated -= h, (_, _) => to.GrabFocus()));

    private async Task<string?> RegisterAsync()
    {
        var email = _email.GetText().Trim();
        var result = await _profile.RegisterWithProfileAsync(email, _password.GetText(), _name.GetText().Trim());
        if (result.IsSuccess && result.Account is not null)
        {
            _registered = result.Account.Email;
            return null;
        }
        return WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
    }

#if DEBUG
    internal static WinoAccountRegistrationDialog? Current { get; private set; }

    internal string Describe() => $"error='{_form.ErrorText}'";

    internal void DebugSet(string field, string text)
    {
        var row = field switch { "email" => _email, "name" => _name, "password" => _password, "confirm" => (Adw.EntryRow)_confirm, _ => null };
        row?.SetText(text);
    }

    internal void DebugConfirm() => _form.DebugConfirm();

    internal void DebugClose() => _form.Dialog.Close();
#endif
}
