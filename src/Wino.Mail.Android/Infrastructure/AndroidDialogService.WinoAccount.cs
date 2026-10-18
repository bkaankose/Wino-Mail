using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// Wino Account dialogs (the iOS UIKitDialogService.WinoAccount partial and its sheets, ports of the Windows dialogs):
/// sign-in with forgot password and the email confirmation follow-up, registration, and the sync secret. Purchases are
/// not part of Android v1 (no Play Billing): the purchase channel dialog is never offered (the shared ViewModels show
/// purchase entry points only when a store capability is set).
/// </summary>
public sealed partial class AndroidDialogService
{
    private sealed record LoginOutcome(WinoAccount? Account, string? PendingConfirmationEmail = null,
        EmailConfirmationRequiredDetailsDto? ConfirmationDetails = null, string? PasswordResetEmail = null);

    /// <summary>Sign-in, then the confirmation dialog or the reset-sent message when sign-in asks for it.</summary>
    public async Task<WinoAccount?> ShowWinoAccountLoginDialogAsync()
    {
        if (Profile() is not { } profile) return null;
        var externalSignIn = services.GetService<IWinoAccountExternalSignInService>();
        var outcome = await FormAsync<LoginOutcome?>(activity =>
        {
            bool forgot = false;
            var form = new AndroidFormDialog<LoginOutcome?>(activity, Translator.WinoAccount_LoginDialog_Title, null, Translator.Buttons_SignIn);
            var description = form.AddText(Translator.WinoAccount_LoginDialog_Description);
            var email = form.AddTextField(Translator.WinoAccount_EmailLabel, email: true, helper: Translator.WinoAccount_EmailPlaceholder);
            var password = form.AddTextField(Translator.WinoAccount_PasswordLabel, password: true);
            var passwordLayout = (View?)password.Parent?.Parent ?? password;
            var link = form.Add(Link(activity, Translator.WinoAccount_LoginDialog_ForgotPasswordLink), Resource.Dimension.wino_space_2xs);
            // Sign in with Google (Credential Manager), Apple (the API browser flow) and Microsoft (the browser flow) under
            // an "or" rule, Google first on Android; hidden in forgot-password mode.
            var external = new List<View>();
            if (externalSignIn is { IsAppleAvailable: true } or { IsGoogleAvailable: true } or { IsMicrosoftAvailable: true })
                external.Add(form.Add(OrRule(activity), Resource.Dimension.wino_space_m));
            if (externalSignIn is { IsGoogleAvailable: true })
                external.Add(ExternalButton(form.Add(GoogleButton(activity), Resource.Dimension.wino_space_s), token => externalSignIn.SignInWithGoogleAsync(token)));
            if (externalSignIn is { IsAppleAvailable: true })
                external.Add(ExternalButton(form.Add(AppleButton(activity), Resource.Dimension.wino_space_s), token => externalSignIn.SignInWithAppleAsync(token)));
            if (externalSignIn is { IsMicrosoftAvailable: true })
                external.Add(ExternalButton(form.Add(MicrosoftButton(activity), Resource.Dimension.wino_space_s), token => externalSignIn.SignInWithMicrosoftAsync(token)));
            void UpdateMode()
            {
                form.Title = forgot ? Translator.WinoAccount_ForgotPasswordDialog_Title : Translator.WinoAccount_LoginDialog_Title;
                description.Text = forgot ? Translator.WinoAccount_ForgotPasswordDialog_Description : Translator.WinoAccount_LoginDialog_Description;
                form.PrimaryText = forgot ? Translator.WinoAccount_ForgotPasswordDialog_PrimaryButton : Translator.Buttons_SignIn;
                passwordLayout.Visibility = forgot ? ViewStates.Gone : ViewStates.Visible;
                foreach (var view in external) view.Visibility = forgot ? ViewStates.Gone : ViewStates.Visible;
                link.Text = forgot ? Translator.WinoAccount_ForgotPasswordDialog_BackToSignIn : Translator.WinoAccount_LoginDialog_ForgotPasswordLink;
                form.Refresh();
            }
            MaterialButton ExternalButton(MaterialButton button, Func<CancellationToken, Task<WinoAccountOperationResult>> signIn)
            {
                button.Click += async (_, _) =>
                {
                    if (form.IsBusy) return;
                    form.ShowError(null);
                    form.SetBusy(true);
                    foreach (var view in external) view.Enabled = false;
                    string? error = null;
                    try
                    {
                        var result = await signIn(CancellationToken.None);
                        if (result.IsSuccess && result.Account is not null) { form.Finish(new LoginOutcome(result.Account)); return; }
                        if (!WinoAccountClientErrorCodes.IsCancelled(result.ErrorCode))
                            error = WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
                    }
                    catch (Exception exception)
                    {
                        Serilog.Log.Warning(exception, "External sign-in failed.");
                        error = exception.Message;
                    }
                    finally
                    {
                        form.SetBusy(false);
                        foreach (var view in external) view.Enabled = true;
                    }
                    form.ShowError(error);
                };
                return button;
            }
            link.Click += (_, _) =>
            {
                forgot = !forgot;
                password.Text = string.Empty;
                form.ShowError(null);
                UpdateMode();
            };
            form.Closed = () => password.Text = string.Empty;
            form.Primary = async () =>
            {
                var address = (email.Text ?? string.Empty).Trim();
                var secret = password.Text ?? string.Empty;
                var validation = string.IsNullOrWhiteSpace(address) ? Translator.WinoAccount_Validation_EmailRequired
                    : !forgot && string.IsNullOrWhiteSpace(secret) ? Translator.WinoAccount_Validation_PasswordRequired
                    : null;
                if (validation is not null) { form.ShowError(validation); return; }

                form.ShowError(null);
                form.SetBusy(true);
                string? error;
                try
                {
                    if (forgot)
                    {
                        var response = await profile.ForgotPasswordAsync(address);
                        if (response.IsSuccess) { form.Finish(new LoginOutcome(null, PasswordResetEmail: address)); return; }
                        error = WinoAccountAuthErrorTranslator.Translate(response.ErrorCode);
                    }
                    else
                    {
                        var result = await profile.LoginAsync(address, secret);
                        if (result.IsSuccess && result.Account is not null) { form.Finish(new LoginOutcome(result.Account)); return; }
                        var details = WinoAccountEmailConfirmationHelper.Parse(result.ErrorDetails);
                        if (WinoAccountEmailConfirmationHelper.IsEmailConfirmationRequiredError(result.ErrorCode) && details is not null)
                        {
                            form.Finish(new LoginOutcome(null, address, details));
                            return;
                        }
                        error = WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
                    }
                }
                finally { form.SetBusy(false); }
                form.ShowError(error);
            };
            return form;
        }, null);

        if (outcome is { ConfirmationDetails: { } confirmation, PendingConfirmationEmail: { Length: > 0 } pending })
        {
            if (await ShowEmailConfirmationAsync(profile, pending, confirmation))
                await ShowMessageAsync(string.Format(Translator.WinoAccount_EmailConfirmationResentDialog_Message, pending), Translator.WinoAccount_EmailConfirmationResentDialog_Title, WinoCustomMessageDialogIcon.Information);
            return null;
        }
        if (outcome is { PasswordResetEmail: { Length: > 0 } reset })
        {
            await ShowMessageAsync(string.Format(Translator.WinoAccount_ForgotPasswordDialog_SuccessMessage, reset), Translator.WinoAccount_ForgotPasswordDialog_SuccessTitle, WinoCustomMessageDialogIcon.Information);
            return null;
        }
        return outcome?.Account;
    }

    /// <summary>
    /// Registration, then "confirmation email sent" for the address. The account is not signed in until the address is
    /// confirmed, so the result is always null (Windows, the Mac and iOS).
    /// </summary>
    public async Task<WinoAccount?> ShowWinoAccountRegistrationDialogAsync()
    {
        if (Profile() is not { } profile) return null;
        var sentTo = await FormAsync<string?>(activity =>
        {
            var form = new AndroidFormDialog<string?>(activity, Translator.WinoAccount_RegisterDialog_Title, Translator.WinoAccount_RegisterDialog_Description,
                Translator.WinoAccount_RegisterDialog_PrimaryButton);
            var email = form.AddTextField(Translator.WinoAccount_EmailLabel, email: true, helper: Translator.WinoAccount_EmailPlaceholder);
            var name = form.AddTextField(Translator.WinoAccount_Profile_NameOptional);
            var password = form.AddTextField(Translator.WinoAccount_PasswordLabel, password: true);
            var confirm = form.AddTextField(Translator.WinoAccount_ConfirmPasswordLabel, password: true);
            form.AddText(Translator.WinoAccount_RegisterDialog_PrivacyAgreement);
            var privacy = form.Add(Link(activity, Translator.WinoAccount_RegisterDialog_PrivacyLinkText), Resource.Dimension.wino_space_3xs);
            privacy.Click += (_, _) => _ = services.GetService<IExternalLauncher>()?.LaunchUriAsync(new Uri(AppUrls.PrivacyPolicy));
            form.Closed = () =>
            {
                password.Text = string.Empty;
                confirm.Text = string.Empty;
            };
            form.Primary = async () =>
            {
                var address = (email.Text ?? string.Empty).Trim();
                var secret = password.Text ?? string.Empty;
                var validation = string.IsNullOrWhiteSpace(address) ? Translator.WinoAccount_Validation_EmailRequired
                    : string.IsNullOrWhiteSpace(secret) ? Translator.WinoAccount_Validation_PasswordRequired
                    : !string.Equals(secret, confirm.Text ?? string.Empty, StringComparison.Ordinal) ? Translator.WinoAccount_Validation_PasswordMismatch
                    : null;
                if (validation is not null) { form.ShowError(validation); return; }

                form.ShowError(null);
                form.SetBusy(true);
                string? error;
                try
                {
                    var result = await profile.RegisterWithProfileAsync(address, secret, (name.Text ?? string.Empty).Trim());
                    if (result.IsSuccess && result.Account is not null) { form.Finish(result.Account.Email); return; }
                    error = WinoAccountAuthErrorTranslator.Format(result.ErrorCode, result.ErrorMessage);
                }
                finally { form.SetBusy(false); }
                form.ShowError(error);
            };
            return form;
        }, null);
        if (!string.IsNullOrWhiteSpace(sentTo))
            await ShowMessageAsync(string.Format(Translator.WinoAccount_EmailConfirmationSentDialog_Message, sentTo), Translator.WinoAccount_EmailConfirmationSentDialog_Title, WinoCustomMessageDialogIcon.Information);
        return null;
    }

    /// <summary>
    /// Email confirmation required (Windows WinoAccountEmailConfirmationRequiredDialog): the pending address, a resend
    /// countdown, Resend enabled when it ends. True when a new confirmation email was sent.
    /// </summary>
    private Task<bool> ShowEmailConfirmationAsync(IWinoAccountProfileService profile, string email, EmailConfirmationRequiredDetailsDto details)
        => FormAsync<bool>(activity =>
        {
            var form = new AndroidFormDialog<bool>(activity, Translator.WinoAccount_EmailConfirmationPendingDialog_Title, null,
                Translator.WinoAccount_EmailConfirmationPendingDialog_ResendButton, Translator.Buttons_Close);
            form.AddText(string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Message, email), secondary: false);
            var countdown = form.AddText(string.Empty);
            var handler = new Handler(Looper.MainLooper ?? throw new InvalidOperationException("The main looper is unavailable."));
            bool open = true;
            void Tick()
            {
                if (!open) return;
                var remaining = details.ResendAvailableAtUtc - DateTimeOffset.UtcNow;
                countdown.Text = remaining <= TimeSpan.Zero
                    ? Translator.WinoAccount_EmailConfirmationPendingDialog_ReadyToResend
                    : string.Format(Translator.WinoAccount_EmailConfirmationPendingDialog_Countdown, $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}");
                form.Refresh();
                if (remaining > TimeSpan.Zero) handler.PostDelayed(Tick, 1000);
            }
            form.CanConfirm = () => DateTimeOffset.UtcNow >= details.ResendAvailableAtUtc;
            form.Closed = () => open = false;
            form.Primary = async () =>
            {
                form.ShowError(null);
                form.SetBusy(true);
                string? error;
                try
                {
                    var response = await profile.ResendEmailConfirmationAsync(details.ResendConfirmationEndpoint, details.ResendConfirmationTicket);
                    if (response.IsSuccess) { form.Finish(true); return; }
                    error = WinoAccountAuthErrorTranslator.Translate(response.ErrorCode);
                }
                finally { form.SetBusy(false); }
                form.ShowError(error);
            };
            Tick();
            return form;
        }, false);

    /// <summary>
    /// Asks for the password that unlocks a sync snapshot, or has the user choose and confirm one for a new backup
    /// (Windows WinoAccountSyncSecretDialog). The value goes back to the caller only: never logged or stored, and the
    /// fields are cleared when the dialog closes. Null when cancelled.
    /// </summary>
    public Task<string?> ShowWinoAccountSyncSecretDialogAsync(SyncSnapshotSecretRequest request) => FormAsync<string?>(activity =>
    {
        bool isNew = request.IsNewBackup;
        var description = isNew
            ? Translator.WinoAccount_Sync_SecretDialog_NewDescription_Device
            : request.IsPassphrase ? Translator.WinoAccount_Sync_SecretDialog_PassphraseDescription : Translator.WinoAccount_Sync_SecretDialog_PasswordDescription;
        var label = isNew || request.IsPassphrase ? Translator.WinoAccount_Sync_SecretDialog_PassphraseLabel : Translator.WinoAccount_Sync_SecretDialog_PasswordLabel;
        var form = new AndroidFormDialog<string?>(activity,
            isNew ? Translator.WinoAccount_Sync_SecretDialog_NewTitle : Translator.WinoAccount_Sync_SecretDialog_Title, description,
            isNew ? Translator.Buttons_Continue : Translator.Buttons_Unlock);
        var secret = form.AddTextField(label, password: true);
        EditText? confirm = isNew ? form.AddTextField(Translator.WinoAccount_Sync_SecretDialog_ConfirmLabel, password: true) : null;
        if (request.WasRejected) form.ShowError(Translator.WinoAccount_Sync_SecretDialog_Rejected);
        form.CanConfirm = () => !string.IsNullOrEmpty(secret.Text) && (confirm is null || !string.IsNullOrEmpty(confirm.Text));
        form.Closed = () =>
        {
            secret.Text = string.Empty;
            if (confirm is not null) confirm.Text = string.Empty;
        };
        form.Primary = () =>
        {
            var value = secret.Text ?? string.Empty;
            if (value.Length == 0) form.ShowError(Translator.WinoAccount_Sync_SecretDialog_Required);
            else if (confirm is not null && value != (confirm.Text ?? string.Empty))
            {
                form.ShowError(Translator.WinoAccount_Sync_SecretDialog_Mismatch);
                confirm.RequestFocus();
            }
            else form.Finish(value);
            return Task.CompletedTask;
        };
        return form;
    }, null);

    /// <summary>
    /// Purchases are not part of Android v1 (no Play Billing; plan section 1). The shared ViewModels offer purchase entry
    /// points only when a store capability is set, so this is not reached; it answers like Cancel without a message.
    /// </summary>
    public Task<UnlimitedAccountsPurchaseChannel?> ShowUnlimitedAccountsPurchaseChannelDialogAsync()
    {
        Serilog.Log.Information("Purchases are not available on Android; the purchase channel dialog was not shown.");
        return Task.FromResult<UnlimitedAccountsPurchaseChannel?>(null);
    }

    private IWinoAccountProfileService? Profile()
    {
        var profile = services.GetService<IWinoAccountProfileService>();
        if (profile is null) Serilog.Log.Warning("The Wino Account profile service is not registered; the Wino Account dialog cannot open.");
        return profile;
    }

    /// <summary>A hairline, "or", a hairline: the rule between the password form and the external sign-in buttons.</summary>
    private static LinearLayout OrRule(global::Android.Content.Context context)
    {
        var rule = new LinearLayout(context) { Orientation = global::Android.Widget.Orientation.Horizontal, ImportantForAccessibility = ImportantForAccessibility.No };
        rule.SetGravity(GravityFlags.CenterVertical);
        var label = WinoTokens.Style(new TextView(context) { Text = Translator.WinoAccount_LoginDialog_OrDivider }, WinoTextStyle.BodySecondary);
        var gap = WinoTokens.Px(context, Resource.Dimension.wino_space_sm);
        rule.AddView(Hairline(), new LinearLayout.LayoutParams(0, WinoTokens.Px(context, Resource.Dimension.wino_space_hairline), 1f));
        rule.AddView(label, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = gap, MarginEnd = gap });
        rule.AddView(Hairline(), new LinearLayout.LayoutParams(0, WinoTokens.Px(context, Resource.Dimension.wino_space_hairline), 1f));
        return rule;

        View Hairline() => new(context) { Background = new global::Android.Graphics.Drawables.ColorDrawable(WinoColors.Divider(context)) };
    }

    /// <summary>
    /// Sign in with Apple as Apple's web/Android guidelines draw it: black fill, white Apple mark and text (a hairline
    /// outline in the dark theme so it reads on a dark dialog). The mark is the WinoIcons Apple glyph.
    /// </summary>
    private static MaterialButton AppleButton(global::Android.Content.Context context)
    {
        var dark = WinoColors.IsDark(context);
        var button = new MaterialButton(context)
        {
            Text = Translator.WinoAccount_SignInWithApple,
            ContentDescription = Translator.WinoAccount_SignInWithApple,
            IconGravity = MaterialButton.IconGravityTextStart,
            IconPadding = WinoTokens.Px(context, Resource.Dimension.wino_space_s)
        };
        button.SetAllCaps(false);
        button.InsetTop = 0;
        button.InsetBottom = 0;
        button.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_pill_button_height));
        button.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_pill_button_height));
        button.CornerRadius = WinoTokens.Px(context, Resource.Dimension.wino_radius_large);
        button.SetTextColor(Color.White);
        button.BackgroundTintList = ColorStateList.ValueOf(Color.Black);
        button.Icon = WinoIcons.Drawable(context, WinoIconGlyph.Apple, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_ml), Color.White);
        button.IconTint = ColorStateList.ValueOf(Color.White);
        if (dark)
        {
            button.StrokeWidth = WinoTokens.Px(context, Resource.Dimension.wino_space_hairline);
            button.StrokeColor = ColorStateList.ValueOf(WinoColors.Outline(context));
        }
        return button;
    }

    /// <summary>Sign in with Google as Google's guidelines draw it: the colourful mark and the title on a neutral, outlined pill.</summary>
    private static MaterialButton GoogleButton(global::Android.Content.Context context)
        => OutlinedProviderButton(context, Translator.WinoAccount_SignInWithGoogle, WinoIconGlyph.Google);

    /// <summary>Sign in with Microsoft as the brand guidelines draw it: the four-colour mark and the title on a neutral, outlined pill.</summary>
    private static MaterialButton MicrosoftButton(global::Android.Content.Context context)
        => OutlinedProviderButton(context, Translator.WinoAccount_SignInWithMicrosoft, WinoIconGlyph.Microsoft);

    private static MaterialButton OutlinedProviderButton(global::Android.Content.Context context, string title, WinoIconGlyph glyph)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            Text = title,
            ContentDescription = title,
            IconGravity = MaterialButton.IconGravityTextStart,
            IconPadding = WinoTokens.Px(context, Resource.Dimension.wino_space_s)
        };
        button.SetAllCaps(false);
        button.InsetTop = 0;
        button.InsetBottom = 0;
        button.SetMinHeight(WinoTokens.Px(context, Resource.Dimension.wino_pill_button_height));
        button.SetMinimumHeight(WinoTokens.Px(context, Resource.Dimension.wino_pill_button_height));
        button.CornerRadius = WinoTokens.Px(context, Resource.Dimension.wino_radius_large);
        button.SetTextColor(WinoColors.PrimaryText(context));
        button.Icon = WinoIcons.Drawable(context, glyph, WinoTokens.Dp(context, Resource.Dimension.wino_glyph_ml), colorful: true);
        button.IconTint = null;
        button.StrokeWidth = WinoTokens.Px(context, Resource.Dimension.wino_space_hairline);
        button.StrokeColor = ColorStateList.ValueOf(WinoColors.Outline(context));
        return button;
    }

    /// <summary>A text button in the accent text colour (the Windows HyperlinkButton).</summary>
    private static TextView Link(global::Android.Content.Context context, string text)
    {
        var link = WinoTokens.Style(new TextView(context) { Text = text, Clickable = true, Focusable = true }, WinoTextStyle.Body);
        link.SetTextColor(WinoColors.AccentText(context));
        link.SetPadding(0, WinoTokens.Px(context, Resource.Dimension.wino_space_xs), 0, WinoTokens.Px(context, Resource.Dimension.wino_space_xs));
        return link;
    }
}
