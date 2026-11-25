using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Account;

/// <summary>What the sign-in dialog ended with; the dialog service shows the follow-up dialog or alert.</summary>
internal sealed record WinoAccountLoginOutcome(
    WinoAccount? Account,
    string? PendingConfirmationEmail = null,
    EmailConfirmationRequiredDetailsDto? ConfirmationDetails = null,
    string? PasswordResetEmail = null);

/// <summary>
/// Sign in to Wino Account (Windows WinoAccountLoginDialog, macOS WinoAccountLoginSheet): e-mail and password, a
/// forgot-password mode inside the same dialog, and Apple, Google and Microsoft under an "or" rule. The three external
/// buttons run the API's browser flow; the dialog's own busy state is the waiting UI, as on Windows.
/// </summary>
internal sealed class WinoAccountLoginDialog
{
    private readonly IWinoAccountProfileService _profile;
    private readonly IWinoAccountExternalSignInService? _externalSignIn;
    private readonly FormDialog _form = new(Translator.WinoAccount_LoginDialog_Title, null, Translator.Buttons_SignIn);
    private readonly Gtk.Label _description = Gtk.Label.New(Translator.WinoAccount_LoginDialog_Description);
    private readonly Adw.EntryRow _email = WinoSettingsRows.Entry(Translator.WinoAccount_EmailLabel);
    private readonly Adw.PasswordEntryRow _password = WinoSettingsRows.Password(Translator.WinoAccount_PasswordLabel);
    private readonly Gtk.Button _modeLink = Gtk.Button.NewWithLabel(Translator.WinoAccount_LoginDialog_ForgotPasswordLink);
    private readonly Gtk.Box _external = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
    private readonly Adw.Spinner _spinner = Adw.Spinner.New();
    private readonly List<Gtk.Button> _externalButtons = [];
    private readonly CancellationTokenSource _closed = new();
    private WinoAccountLoginOutcome? _outcome;
    private bool _forgot;
    private bool _busy;

    public WinoAccountLoginDialog(IWinoAccountProfileService profile, IWinoAccountExternalSignInService? externalSignIn)
    {
        _profile = profile;
        _externalSignIn = externalSignIn;
        _form.ContentWidth = 440;

        _description.SetWrap(true);
        _description.SetXalign(0);
        _description.AddCssClass("dim-label");
        _form.Add(_description);

        _email.SetInputPurpose(Gtk.InputPurpose.Email);
        // Windows: Return in the e-mail field moves to the password; only Return in the password signs in.
        _email.SetActivatesDefault(false);
        _password.SetActivatesDefault(true);
        var group = _form.AddGroup();
        group.Add(_email);
        group.Add(_password);
        _modeLink.AddCssClass("link");
        _modeLink.SetHalign(Gtk.Align.Start);
        _form.Add(_modeLink);

        if (externalSignIn is { IsAppleAvailable: true }) AddExternal(WinoIconGlyph.Apple, Translator.WinoAccount_SignInWithApple, token => externalSignIn.SignInWithAppleAsync(token));
        if (externalSignIn is { IsGoogleAvailable: true }) AddExternal(WinoIconGlyph.Google, Translator.WinoAccount_SignInWithGoogle, token => externalSignIn.SignInWithGoogleAsync(token));
        if (externalSignIn is { IsMicrosoftAvailable: true }) AddExternal(WinoIconGlyph.Microsoft, Translator.WinoAccount_SignInWithMicrosoft, token => externalSignIn.SignInWithMicrosoftAsync(token));
        if (_externalButtons.Count > 0)
        {
            _external.Prepend(OrRule());
            _spinner.SetVisible(false);
            _spinner.SetHalign(Gtk.Align.Center);
            _external.Append(_spinner);
            _form.Add(_external);
        }

        _form.CanConfirm = () => !_busy;
        _form.Validate = () =>
        {
            if (string.IsNullOrWhiteSpace(_email.GetText())) return Translator.WinoAccount_Validation_EmailRequired;
            if (!_forgot && string.IsNullOrWhiteSpace(_password.GetText())) return Translator.WinoAccount_Validation_PasswordRequired;
            return null;
        };
        _form.ValidateAsync = PrimaryAsync;
        _form.Own(GtkSignals.Connect<Adw.EntryRow>(h => _email.OnEntryActivated += h, h => _email.OnEntryActivated -= h, (_, _) =>
        {
            if (!_forgot) _password.GrabFocus();
        }));
        _form.Own(GtkSignals.OnChanged(_email, () => _form.ShowError(null)));
        _form.Own(GtkSignals.OnChanged(_password, () => _form.ShowError(null)));
        _form.Own(GtkSignals.OnClicked(_modeLink, ToggleMode));
        _form.Own(new ActionDisposable(() =>
        {
            _closed.Cancel();
            _password.SetText(string.Empty);
        }));
        _form.SetInitialFocus(_email);
        WinoLiveObjects.Track(this);
    }

    /// <summary>Main thread; null when the user cancelled.</summary>
    public async Task<WinoAccountLoginOutcome?> PresentAsync(Gtk.Widget owner)
    {
#if DEBUG
        Current = this;
#endif
        try
        {
            var confirmed = await _form.PresentAsync(owner);
            return confirmed ? _outcome : null;
        }
        finally
        {
#if DEBUG
            if (ReferenceEquals(Current, this)) Current = null;
#endif
            _closed.Dispose();
        }
    }

    private void AddExternal(WinoIconGlyph glyph, string title, Func<CancellationToken, Task<WinoAccountOperationResult>> signIn)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        content.SetHalign(Gtk.Align.Center);
        content.Append(new WinoIcon(glyph, 16, WinoIconMode.Colorful).Root);
        content.Append(Gtk.Label.New(title));
        var button = Gtk.Button.New();
        button.SetChild(content);
        button.SetSizeRequest(-1, 36);
        WinoA11y.Rename(button, title);
        _form.Own(GtkSignals.OnClicked(button, () => _ = SignInExternallyAsync(signIn)));
        _externalButtons.Add(button);
        _external.Append(button);
    }

    private static Gtk.Widget OrRule()
    {
        var rule = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        var leading = Gtk.Separator.New(Gtk.Orientation.Horizontal);
        var trailing = Gtk.Separator.New(Gtk.Orientation.Horizontal);
        leading.SetHexpand(true);
        trailing.SetHexpand(true);
        leading.SetValign(Gtk.Align.Center);
        trailing.SetValign(Gtk.Align.Center);
        var label = Gtk.Label.New(Translator.WinoAccount_LoginDialog_OrDivider);
        label.AddCssClass("dim-label");
        label.AddCssClass("caption");
        rule.Append(leading);
        rule.Append(label);
        rule.Append(trailing);
        rule.SetMarginTop(6);
        return rule;
    }

    private void ToggleMode()
    {
        _forgot = !_forgot;
        _password.SetText(string.Empty);
        _form.ShowError(null);
        var title = _forgot ? Translator.WinoAccount_ForgotPasswordDialog_Title : Translator.WinoAccount_LoginDialog_Title;
        _form.Title = title;
        _description.SetText(_forgot ? Translator.WinoAccount_ForgotPasswordDialog_Description : Translator.WinoAccount_LoginDialog_Description);
        _form.PrimaryText = _forgot ? Translator.WinoAccount_ForgotPasswordDialog_PrimaryButton : Translator.Buttons_SignIn;
        _password.SetVisible(!_forgot);
        _email.SetActivatesDefault(_forgot);
        _external.SetVisible(!_forgot && _externalButtons.Count > 0);
        _modeLink.SetLabel(_forgot ? Translator.WinoAccount_ForgotPasswordDialog_BackToSignIn : Translator.WinoAccount_LoginDialog_ForgotPasswordLink);
        WinoA11y.Rename(_modeLink, _forgot ? Translator.WinoAccount_ForgotPasswordDialog_BackToSignIn : Translator.WinoAccount_LoginDialog_ForgotPasswordLink);
    }

    private async Task<string?> PrimaryAsync()
    {
        var email = _email.GetText().Trim();
        SetBusy(true);
        try
        {
            if (_forgot)
            {
                var response = await _profile.ForgotPasswordAsync(email, _closed.Token);
                if (response.IsSuccess)
                {
                    _outcome = new WinoAccountLoginOutcome(null, PasswordResetEmail: email);
                    return null;
                }
                return WinoAccountAuthErrorTranslator.Translate(response.ErrorCode);
            }

            var result = await _profile.LoginAsync(email, _password.GetText(), _closed.Token);
            if (result.IsSuccess && result.Account is not null)
            {
                _outcome = new WinoAccountLoginOutcome(result.Account);
                return null;
            }
            var details = WinoAccountEmailConfirmationHelper.Parse(result.ErrorDetails);
            if (WinoAccountEmailConfirmationHelper.IsEmailConfirmationRequiredError(result.ErrorCode) && details is not null)
            {
                _outcome = new WinoAccountLoginOutcome(null, email, details);
                return null;
            }
            return WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
        }
        catch (OperationCanceledException) { return null; }
        finally { SetBusy(false); }
    }

    /// <summary>The API's browser flow; closing the dialog cancels it, and a cancelled sign-in shows nothing.</summary>
    private async Task SignInExternallyAsync(Func<CancellationToken, Task<WinoAccountOperationResult>> signIn)
    {
        if (_busy || _externalSignIn is null || _closed.IsCancellationRequested) return;
        SetBusy(true);
        _spinner.SetVisible(true);
        _form.ShowError(null);
        string? error = null;
        try
        {
            var result = await signIn(_closed.Token);
            if (_closed.IsCancellationRequested) return;
            if (result.IsSuccess && result.Account is not null)
            {
                _outcome = new WinoAccountLoginOutcome(result.Account);
                _form.Close(true);
                return;
            }
            if (!WinoAccountClientErrorCodes.IsCancelled(result.ErrorCode))
                error = WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) { error = exception.Message; }
        finally
        {
            if (!_closed.IsCancellationRequested)
            {
                _spinner.SetVisible(false);
                SetBusy(false);
            }
        }
        if (error is not null) _form.ShowError(error);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _email.SetSensitive(!busy);
        _password.SetSensitive(!busy);
        _modeLink.SetSensitive(!busy);
        foreach (var button in _externalButtons) button.SetSensitive(!busy);
        _form.Refresh();
    }

#if DEBUG
    internal static WinoAccountLoginDialog? Current { get; private set; }

    internal string Describe()
        => $"mode={(_forgot ? "forgot" : "signin")} busy={_busy} external={_externalButtons.Count} externalVisible={_external.GetVisible()} passwordVisible={_password.GetVisible()} error='{_form.ErrorText}'";

    internal void DebugSetEmail(string text) => _email.SetText(text);

    internal void DebugSetPassword(string text) => _password.SetText(text);

    internal void DebugToggleMode() => ToggleMode();

    internal void DebugConfirm() => _form.DebugConfirm();

    internal void DebugExternal(int index)
    {
        if (index >= 0 && index < _externalButtons.Count) _externalButtons[index].Activate();
    }

    internal void DebugClose() => _form.Dialog.Close();
#endif
}
