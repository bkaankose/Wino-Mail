using Android.Content;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.CheckBox;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.RadioButton;
using Google.Android.Material.TextField;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;
using Wino.Presentation.Bindings;
using Form = Wino.Mail.Android.Views.Onboarding.OnboardingForm<Wino.Mail.ViewModels.AccountSetupPageViewModel>;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>The steps of <see cref="AccountSetupPageFragment"/>, in the order of the Windows step templates.</summary>
public sealed partial class AccountSetupPageFragment
{
    private const InputTypes EmailInput = InputTypes.ClassText | InputTypes.TextVariationEmailAddress;
    private const InputTypes PasswordInput = InputTypes.ClassText | InputTypes.TextVariationPassword;

    #region Address

    private void BuildAddressStep(Form form, AccountSetupPageViewModel viewModel)
    {
        var context = form.Context;

        // The providers Wino connects to. They are not buttons: the typed domain lights its provider.
        var shelf = new LinearLayout(context) { Orientation = Orientation.Horizontal, ContentDescription = Translator.AccountSetup_Shelf_Header };
        shelf.SetGravity(GravityFlags.Center);
        var shelfScroll = new HorizontalScrollView(context) { HorizontalScrollBarEnabled = false, FillViewport = true };
        shelfScroll.AddView(shelf, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        form.Add(shelfScroll);
        form.Binder.BindAny(viewModel, vm => vm.IsProviderShelfVisible, visible => OnboardingViews.Show(shelfScroll, visible));
        form.Binder.Bind(viewModel, nameof(viewModel.ProviderShelfItems), vm => vm.ProviderShelfItems, items =>
        {
            shelf.RemoveAllViews();
            foreach (var item in items)
            {
                var parameters = new LinearLayout.LayoutParams(item.HasLabel ? ViewGroup.LayoutParams.WrapContent : WinoUnits.Dp(context, 36), WinoUnits.Dp(context, 36));
                if (shelf.ChildCount > 0) parameters.MarginStart = WinoUnits.Dp(context, 8);
                shelf.AddView(ShelfItem(context, item), parameters);
            }
        });

        form.GroupWhen(vm => vm.IsEmailFieldVisible, () =>
        {
            var email = form.Field(Translator.AccountSetup_EmailHeader, nameof(viewModel.EmailAddress), vm => vm.EmailAddress,
                (vm, value) => vm.EmailAddress = value, EmailInput);
            email.PlaceholderText = Translator.AccountSetup_EmailPlaceholder;
            SubmitOnEnter(email.EditText, form.Binder, viewModel);
            // A recognised address shows its provider's mark inside the field.
            form.Binder.BindAny(viewModel, vm => (vm.HasMatchedProvider, vm.MatchedProviderGlyph), state =>
            {
                email.StartIconDrawable = state.HasMatchedProvider ? WinoIcons.Drawable(context, state.MatchedProviderGlyph, 18, colorful: true) : null;
                email.SetStartIconTintList(null);
            });
            form.Binder.BindAny(viewModel, vm => (vm.HasIdentityError, vm.IdentityError, vm.ProviderShelfCaption), state =>
            {
                email.Error = state.HasIdentityError ? state.IdentityError : null;
                email.HelperText = state.HasIdentityError ? null : state.ProviderShelfCaption;
            });
        });

        form.GroupWhen(vm => vm.IsLocalOnlyMode, () => AccountNameRow(form, viewModel));
    }

    /// <summary>A shelf item: a provider mark in a circle, or a label pill (IMAP, POP3); the matched one has an accent ring.</summary>
    private static View ShelfItem(Context context, AccountSetupProviderShelfItem item)
    {
        var background = new GradientDrawable();
        background.SetColor(WinoColors.SubtleFill(context));
        if (item.IsMatched) background.SetStroke(WinoUnits.Dp(context, 2), WinoColors.Accent(context));
        View view;
        if (item.HasGlyph)
        {
            background.SetShape(ShapeType.Oval);
            view = new WinoIconView(context) { Icon = item.Glyph, GlyphSize = 18, Colorful = null, Background = background };
        }
        else
        {
            background.SetCornerRadius(WinoUnits.DpF(context, 18));
            var label = new TextView(context) { Text = item.Label, Gravity = GravityFlags.Center, Background = background };
            label.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
            label.SetTypeface(label.Typeface, global::Android.Graphics.TypefaceStyle.Bold);
            label.SetTextColor(WinoColors.PrimaryText(context));
            label.SetPadding(WinoUnits.Dp(context, 12), 0, WinoUnits.Dp(context, 12), 0);
            view = label;
        }
        view.ContentDescription = item.Title;
        view.ImportantForAccessibility = ImportantForAccessibility.Yes;
        TooltipCompatSet(view, item.Title);
        view.Alpha = item.IsDimmed ? 0.4f : 1f;
        return view;
    }

    private static void TooltipCompatSet(View view, string text) => AndroidX.AppCompat.Widget.TooltipCompat.SetTooltipText(view, text);

    /// <summary>The account name with the colour button beside it, and the name's error (address and review steps).</summary>
    private void AccountNameRow(Form form, AccountSetupPageViewModel viewModel)
    {
        var context = form.Context;
        TextInputLayout? name = null;
        form.Row(() =>
        {
            name = form.Field(Translator.AccountSetup_AccountNameHeader, nameof(viewModel.AccountName), vm => vm.AccountName,
                (vm, value) => vm.AccountName = value, InputTypes.ClassText | InputTypes.TextFlagCapWords);
            name.PlaceholderText = Translator.AccountSetup_AccountNamePlaceholder;
            SubmitOnEnter(name.EditText, form.Binder, viewModel);

            var color = new WinoIconView(context)
            {
                ContentDescription = Translator.AccountDetailsPage_ColorPicker_Title,
                Clickable = true,
                Focusable = true
            };
            TooltipCompatSet(color, Translator.AccountDetailsPage_ColorPicker_Title);
            form.Binder.Click(color, () => ShowColorPicker(viewModel));
            form.Add(color, wrap: true);
            if (color.LayoutParameters is LinearLayout.LayoutParams parameters)
            {
                parameters.Width = WinoUnits.Dp(context, 48);
                parameters.Height = WinoUnits.Dp(context, 48);
                parameters.Gravity = GravityFlags.CenterVertical;
                parameters.TopMargin = WinoUnits.Dp(context, 12);
            }
            form.Binder.BindAny(viewModel, vm => vm.SelectedColor?.Hex, hex =>
            {
                var fill = WinoColors.FromHexString(hex);
                var circle = new GradientDrawable();
                circle.SetShape(ShapeType.Oval);
                circle.SetColor(fill ?? global::Android.Graphics.Color.Transparent);
                circle.SetStroke(WinoUnits.Dp(context, 1), WinoColors.Outline(context));
                color.Background = new InsetDrawable(circle, WinoUnits.Dp(context, 8));
                color.Icon = fill is null ? WinoIconGlyph.PaintBrush : WinoIconGlyph.None;
                color.GlyphSize = 16;
            });
        });
        if (name is not null)
            form.Binder.BindAny(viewModel, vm => vm.HasAccountNameError ? vm.AccountNameError : null, error => name.Error = error);
    }

    #endregion

    #region Discovery

    private void BuildDiscoveryStep(Form form, AccountSetupPageViewModel viewModel)
    {
        form.ShowWhen(form.Help(viewModel.DiscoverySlowText), vm => vm.IsResolveSlow);

        form.GroupWhen(vm => vm.IsNotFoundVisible, () =>
        {
            var bar = form.InfoBar(WinoInfoBarSeverity.Warning);
            form.Binder.BindAny(viewModel, vm => (vm.NotFoundTitle, vm.NotFoundMessage, vm.IsNotFoundVisible), state =>
            {
                bar.Title = state.NotFoundTitle;
                bar.Message = state.NotFoundMessage;
                bar.IsOpen = state.IsNotFoundVisible;
            });
            Actions(form, bar, flow =>
            {
                var manual = ActionButton(form, flow, string.Empty, viewModel.OpenAdvancedSetupCommand, ButtonKind.Filled);
                form.Binder.BindAny(viewModel, vm => vm.NotFoundManualText, text => manual.Text = text);
                ActionButton(form, flow, Translator.AccountSetup_Retry, viewModel.ResolveAgainCommand, ButtonKind.Outlined);
                var choose = ActionButton(form, flow, Translator.AccountSetup_ChooseProvider, null, ButtonKind.Text, () => ShowProviderPicker(viewModel));
                form.ShowWhen(choose, vm => vm.IsChooseProviderVisible);
            });
        });
    }

    #endregion

    #region Sign-in method

    private void BuildSignInMethodStep(Form form, AccountSetupPageViewModel viewModel)
    {
        var context = form.Context;
        CaptionWhen(form, vm => vm.HasStepCaption ? vm.StepCaption : null, topDp: 0).AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;
        var group = new RadioGroup(context) { Orientation = Orientation.Vertical, ContentDescription = Translator.AccountSetup_SignInOptionsHeader };
        form.Add(group);
        var buttons = new List<(MaterialRadioButton Button, AccountSetupChoice Choice)>();
        form.Binder.Bind(viewModel, nameof(viewModel.StepChoices), vm => vm.StepChoices, choices =>
        {
            group.RemoveAllViews();
            buttons.Clear();
            foreach (var choice in choices)
            {
                var button = new MaterialRadioButton(context) { Id = View.GenerateViewId(), ContentDescription = choice.AutomationName };
                var label = new SpannableStringBuilder(choice.Label);
                if (choice.HasCaption)
                {
                    var start = label.Length();
                    label.Append("  " + choice.Caption);
                    label.SetSpan(new global::Android.Text.Style.ForegroundColorSpan(WinoColors.SecondaryText(context)), start, label.Length(), SpanTypes.ExclusiveExclusive);
                    label.SetSpan(new global::Android.Text.Style.RelativeSizeSpan(0.86f), start, label.Length(), SpanTypes.ExclusiveExclusive);
                }
                button.TextFormatted = label;
                button.SetMinHeight(WinoUnits.Dp(context, 48));
                button.Click += (_, _) => { if (!ReferenceEquals(viewModel.SelectedChoice, choice)) viewModel.SelectedChoice = choice; };
                group.AddView(button, new RadioGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
                buttons.Add((button, choice));
            }
            Select(viewModel.SelectedChoice);
        });
        void Select(AccountSetupChoice? selected)
        {
            foreach (var (button, choice) in buttons)
                if (ReferenceEquals(choice, selected) && !button.Checked) button.Checked = true;
        }
        form.Binder.Bind(viewModel, nameof(viewModel.SelectedChoice), vm => vm.SelectedChoice, Select);
    }

    #endregion

    #region Sign-in

    private void BuildSignInStep(Form form, AccountSetupPageViewModel viewModel)
    {
        var context = form.Context;

        // The issuer the server names is not one Wino knows: the user confirms it before the browser opens.
        form.GroupWhen(vm => vm.IsIssuerConfirmationVisible, () =>
        {
            var confirm = new MaterialCheckBox(context);
            form.Binder.BindAny(viewModel, vm => vm.IssuerConfirmationText, text => confirm.Text = text);
            form.Binder.Checked(confirm, viewModel, nameof(viewModel.IsIssuerConfirmed), vm => vm.IsIssuerConfirmed, (vm, value) => vm.IsIssuerConfirmed = value);
            form.Add(confirm);
            form.Help(Translator.ImapSetup_OAuthConfirmIssuerDescription, topDp: 0);
        });

        var browser = ProviderButton(context);
        form.Add(browser, wrap: true);
        CenterHorizontally(browser);
        form.Binder.Command(browser, viewModel.SignInWithBrowserCommand);
        form.Binder.BindAny(viewModel, vm => (vm.IsBrowserSignInButtonVisible, vm.BrowserSignInButtonText, vm.RouteSpecialImapProvider), state =>
        {
            OnboardingViews.Show(browser, state.IsBrowserSignInButtonVisible);
            browser.Text = state.BrowserSignInButtonText;
            browser.ContentDescription = state.BrowserSignInButtonText;
            browser.Icon = WinoIcons.Drawable(context, BrowserSignInMark(state.RouteSpecialImapProvider), 18, colorful: true);
            browser.IconTint = null;
        });

        form.GroupWhen(vm => vm.IsPasswordPanelVisible, () => BuildPasswordPanel(form, viewModel));
        form.GroupWhen(vm => vm.IsExchangePasswordVisible, () => BuildExchangePassword(form, viewModel));

        form.GroupWhen(vm => vm.IsSendAsVisible, () =>
        {
            var sendAs = form.Field(Translator.AccountSetup_SendAsHeader, nameof(viewModel.SenderName), vm => vm.SenderName,
                (vm, value) => vm.SenderName = value, InputTypes.ClassText | InputTypes.TextFlagCapWords, Translator.AccountSetup_SendAsDescription);
            SubmitOnEnter(sendAs.EditText, form.Binder, viewModel);
        });

        ConnectionStatus(form, viewModel);

        FailureBlock(form, viewModel, includeAnotherOption: true);
    }

    /// <summary>The provider's mark on the browser sign-in button when Wino has one, otherwise a globe.</summary>
    private static WinoIconGlyph BrowserSignInMark(SpecialImapProvider provider)
    {
        var glyph = MailProviderGlyphs.GetGlyph(MailProviderType.IMAP4, provider);
        return glyph is WinoIconGlyph.IMAP or WinoIconGlyph.None ? WinoIconGlyph.Globe : glyph;
    }

    private void BuildPasswordPanel(Form form, AccountSetupPageViewModel viewModel)
    {
        form.GroupWhen(vm => vm.IsSetupHintCaptionVisible, () =>
        {
            CaptionWhen(form, vm => vm.SignInCaption, topDp: 0);
            var help = form.Link(string.Empty, viewModel.OpenHelpLinkCommand, () => viewModel.HelpUrl);
            form.Binder.BindAny(viewModel, vm => vm.HelpLinkText, text => help.Text = text);
            form.ShowWhen(help, vm => vm.IsHelpLinkVisible);
        });

        form.GroupWhen(vm => vm.IsRegionVisible, () =>
        {
            if (LayoutInflater.From(form.Context)?.Inflate(Resource.Layout.onboarding_dropdown, null, false) is TextInputLayout region &&
                region.EditText is MaterialAutoCompleteTextView field)
            {
                region.Hint = Translator.ProviderSelection_RegionHeader;
                form.Add(region);
                form.Binder.BindAny(viewModel, vm => (vm.Regions, vm.SelectedRegion), state =>
                {
                    field.SetSimpleItems(state.Regions.Select(item => item.DisplayName).ToArray());
                    var text = state.SelectedRegion?.DisplayName ?? string.Empty;
                    if (field.Text != text) field.SetText(text, false);
                });
                EventHandler<AdapterView.ItemClickEventArgs> chosen = (_, args) =>
                {
                    if (args.Position >= 0 && args.Position < viewModel.Regions.Count) viewModel.SelectedRegion = viewModel.Regions[args.Position];
                };
                field.ItemClick += chosen;
                form.Binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => field.ItemClick -= chosen));
            }
            CaptionWhen(form, vm => vm.RegionDescription, topDp: 4);
        });

        form.GroupWhen(vm => vm.IsLoginAddressVisible, () =>
        {
            var login = form.Field(string.Empty, nameof(viewModel.LoginAddress), vm => vm.LoginAddress, (vm, value) => vm.LoginAddress = value, EmailInput);
            form.Binder.BindAny(viewModel, vm => (vm.LoginAddressHeader, vm.LoginAddressDescription), state =>
            {
                login.Hint = state.LoginAddressHeader;
                login.HelperText = state.LoginAddressDescription;
            });
        });

        form.GroupWhen(vm => vm.IsImapUserNameVisible, () =>
        {
            var user = form.Field(Translator.AccountSetup_UserNameHeader, nameof(viewModel.ImapUserName), vm => vm.ImapUserName,
                (vm, value) => vm.ImapUserName = value, EmailInput, Translator.AccountSetup_UserNameDescription);
            form.Binder.BindAny(viewModel, vm => vm.ImapUserNamePlaceholder, text => user.PlaceholderText = text);
            SubmitOnEnter(user.EditText, form.Binder, viewModel);
        });

        var password = form.Field(string.Empty, nameof(viewModel.Password), vm => vm.Password, (vm, value) => vm.Password = value, PasswordInput);
        SubmitOnEnter(password.EditText, form.Binder, viewModel);
        form.Binder.BindAny(viewModel, vm => (vm.PasswordHeader, vm.IsConnectionStatusBusy), state =>
        {
            password.Hint = state.PasswordHeader;
            password.Enabled = !state.IsConnectionStatusBusy;
        });

        var otherUser = form.Link(Translator.AccountSetup_UseDifferentUserName, viewModel.ShowImapUserNameCommand);
        form.ShowWhen(otherUser, vm => vm.IsImapUserNameLinkVisible);
    }

    /// <summary>Exchange without modern auth: the sign-in name when it is not the address, then the password.</summary>
    private void BuildExchangePassword(Form form, AccountSetupPageViewModel viewModel)
    {
        CaptionWhen(form, vm => vm.ExchangePasswordCaption, topDp: 0);
        var user = form.Field(Translator.ExchangeSetup_UserNameHeader, nameof(viewModel.ExchangeUserName), vm => vm.ExchangeUserName,
            (vm, value) => vm.ExchangeUserName = value, EmailInput, Translator.ExchangeSetup_UserNameDescription);
        SubmitOnEnter(user.EditText, form.Binder, viewModel);
        var password = form.Field(Translator.ProviderSelection_PasswordHeader_Account, nameof(viewModel.Password), vm => vm.Password,
            (vm, value) => vm.Password = value, PasswordInput);
        SubmitOnEnter(password.EditText, form.Binder, viewModel);
        form.Binder.BindAny(viewModel, vm => (vm.ExchangeUserNamePlaceholder, vm.IsConnectionStatusBusy), state =>
        {
            user.PlaceholderText = state.ExchangeUserNamePlaceholder;
            user.Enabled = !state.IsConnectionStatusBusy;
            password.Enabled = !state.IsConnectionStatusBusy;
        });
    }

    /// <summary>One status line: waiting or checking with a spinner, then a check when every connection works.</summary>
    private static void ConnectionStatus(Form form, AccountSetupPageViewModel viewModel)
    {
        var context = form.Context;
        form.Row(() =>
        {
            var row = StatusLine(context, out var spinner, out var check, out var text);
            form.Add(row, wrap: true);
            form.Binder.BindAny(viewModel, vm => (vm.IsConnectionStatusVisible, vm.IsConnectionStatusBusy, vm.IsConnectionStatusConnected, vm.ConnectionStatusText), state =>
            {
                OnboardingViews.Show(row, state.IsConnectionStatusVisible);
                OnboardingViews.Show(spinner, state.IsConnectionStatusBusy);
                OnboardingViews.Show(check, state.IsConnectionStatusConnected);
                text.Text = state.ConnectionStatusText;
            });
        }, topDp: 12).SetGravity(GravityFlags.Center);
    }

    private static LinearLayout StatusLine(Context context, out View spinner, out View check, out TextView text)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal, AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        row.SetGravity(GravityFlags.CenterVertical);
        var indicator = new CircularProgressIndicator(context) { Indeterminate = true, IndicatorSize = WinoUnits.Dp(context, 16), TrackThickness = WinoUnits.Dp(context, 2) };
        row.AddView(indicator, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = WinoUnits.Dp(context, 8) });
        var glyph = new WinoIconView(context) { Icon = WinoIconGlyph.CheckmarkCircle, GlyphSize = 16, Colorful = false };
        glyph.SetTextColor(WinoColors.Success(context));
        row.AddView(glyph, new LinearLayout.LayoutParams(WinoUnits.Dp(context, 16), WinoUnits.Dp(context, 16)) { MarginEnd = WinoUnits.Dp(context, 8) });
        var label = new TextView(context);
        label.SetTextAppearance(Resource.Style.TextAppearance_Wino_Body);
        row.AddView(label);
        spinner = indicator;
        check = glyph;
        text = label;
        return row;
    }

    /// <summary>
    /// The shared failure as an info bar (closing it hides the failure) with its actions under it: Try again, Use another
    /// option (sign-in only), Change provider, Advanced setup and Show details.
    /// </summary>
    private void FailureBlock(Form form, AccountSetupPageViewModel viewModel, bool includeAnotherOption)
    {
        form.GroupWhen(vm => vm.IsFailureOpen, () =>
        {
            var bar = form.InfoBar(WinoInfoBarSeverity.Error);
            bar.IsClosable = true;
            form.Binder.BindAny(viewModel, vm => (vm.FailureTitle, vm.FailureMessage, vm.FailureSeverity, vm.IsFailureOpen), state =>
            {
                bar.Title = state.FailureTitle;
                bar.Message = state.FailureMessage;
                bar.Severity = state.FailureSeverity switch
                {
                    InfoBarMessageType.Information => WinoInfoBarSeverity.Informational,
                    InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
                    InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
                    _ => WinoInfoBarSeverity.Error
                };
                bar.IsOpen = state.IsFailureOpen;
            });
            EventHandler<WinoInfoBarClosedEventArgs> closed = (_, _) => Run(viewModel.HideFailureCommand);
            bar.Closed += closed;
            form.Binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => bar.Closed -= closed));

            Actions(form, bar, flow =>
            {
                form.ShowWhen(ActionButton(form, flow, Translator.AccountSetup_Retry, viewModel.RetryCommand, ButtonKind.Outlined), vm => vm.IsRetryVisible);
                if (includeAnotherOption)
                    form.ShowWhen(ActionButton(form, flow, Translator.AccountSetup_UseAnotherOption, viewModel.UseAnotherOptionCommand, ButtonKind.Outlined), vm => vm.IsUseAnotherOptionVisible);
                form.ShowWhen(ActionButton(form, flow, Translator.AccountSetup_ChangeProvider, null, ButtonKind.Outlined, () => ShowProviderPicker(viewModel)), vm => vm.IsChangeProviderVisible);
                ActionButton(form, flow, Translator.AccountSetup_AdvancedSetup, viewModel.OpenAdvancedSetupCommand, ButtonKind.Text);
                form.ShowWhen(ActionButton(form, flow, Translator.AccountSetup_ShowDetails, viewModel.ShowFailureDetailsCommand, ButtonKind.Text), vm => vm.HasFailureDetails);
            });
        });
    }

    #endregion

    #region Review

    private void BuildReviewStep(Form form, AccountSetupPageViewModel viewModel)
    {
        var context = form.Context;

        var signedIn = form.InfoBar(WinoInfoBarSeverity.Success);
        signedIn.Message = Translator.AccountSetup_Review_SignedIn;
        form.Binder.BindAny(viewModel, vm => vm.IsReviewSignedInVisible, open => { signedIn.IsOpen = open; OnboardingViews.Show(signedIn, open); });

        var pop3 = form.InfoBar(WinoInfoBarSeverity.Informational, Translator.POP3Setup_LimitationsTitle);
        pop3.Message = Translator.POP3Setup_LimitationsMessage;
        form.Binder.BindAny(viewModel, vm => vm.IsPop3NoticeVisible, open => { pop3.IsOpen = open; OnboardingViews.Show(pop3, open); });

        AccountNameRow(form, viewModel);

        // A browser sign-in returns no display name for IMAP, so the sender name is asked here.
        form.GroupWhen(vm => vm.IsReviewSendAsVisible, () =>
        {
            var sendAs = form.Field(Translator.AccountSetup_SendAsHeader, nameof(viewModel.SenderName), vm => vm.SenderName,
                (vm, value) => vm.SenderName = value, InputTypes.ClassText | InputTypes.TextFlagCapWords, Translator.AccountSetup_SendAsDescription);
            SubmitOnEnter(sendAs.EditText, form.Binder, viewModel);
        });

        // One card per app; the source drop-down, a server address, a note and an error sit with it.
        var rows = form.Group(() => { });
        BindingScope rowScope = new();
        form.Binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => rowScope.Dispose()));
        form.Binder.Bind(viewModel, nameof(viewModel.AppRows), vm => vm.AppRows, appRows =>
        {
            rowScope.Dispose();
            rowScope = new BindingScope();
            var rowBinder = form.Binder.Child(rowScope);
            rows.RemoveAllViews();
            foreach (var row in appRows)
                rows.AddView(AppRow(context, rowBinder, viewModel, row), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                {
                    TopMargin = WinoUnits.Dp(context, 12)
                });
        });
        form.Binder.BindAny(viewModel, vm => !vm.IsReviewStatusVisible, enabled => SetEnabled(rows, enabled));

        var useMailSignIn = new MaterialCheckBox(context) { Text = Translator.AccountSetup_UseMailSignIn };
        form.Binder.Checked(useMailSignIn, viewModel, nameof(viewModel.UseMailSignInForDav), vm => vm.UseMailSignInForDav, (vm, value) => vm.UseMailSignInForDav = value);
        form.Add(useMailSignIn);
        form.ShowWhen(useMailSignIn, vm => vm.IsUseMailSignInForDavVisible);

        form.GroupWhen(vm => vm.IsReviewDavSignInVisible, () =>
        {
            form.Help(Translator.AccountSetup_Review_DavSignInDescription);
            form.Field(Translator.AccountSetup_DavUserNameHeader, nameof(viewModel.DavUserName), vm => vm.DavUserName,
                (vm, value) => vm.DavUserName = value, EmailInput);
            var davPassword = form.Field(Translator.AccountSetup_DavPasswordHeader, nameof(viewModel.DavPassword), vm => vm.DavPassword,
                (vm, value) => vm.DavPassword = value, PasswordInput);
            SubmitOnEnter(davPassword.EditText, form.Binder, viewModel);
        });

        form.GroupWhen(vm => vm.IsServerDetailsVisible, () => ServerDetails(form, viewModel));

        var featuresError = form.Help(topDp: 12);
        featuresError.SetTextColor(WinoColors.Critical(context));
        featuresError.AccessibilityLiveRegion = AccessibilityLiveRegion.Assertive;
        form.Binder.BindAny(viewModel, vm => vm.HasFeaturesError ? vm.FeaturesError : null, text => OnboardingViews.Caption(featuresError, text));

        var caption = CaptionWhen(form, vm => vm.HasReviewCaption ? vm.ReviewCaption : null, topDp: 12);
        caption.AccessibilityLiveRegion = AccessibilityLiveRegion.Polite;

        // Checking an entered server, or waiting for the provider's sign-in page.
        form.Row(() =>
        {
            var line = StatusLine(context, out var spinner, out var check, out var text);
            form.Add(line, wrap: true);
            OnboardingViews.Show(check, false);
            form.Binder.BindAny(viewModel, vm => (vm.IsReviewStatusVisible, vm.ReviewStatusText), state =>
            {
                OnboardingViews.Show(line, state.IsReviewStatusVisible);
                OnboardingViews.Show(spinner, state.IsReviewStatusVisible);
                text.Text = state.ReviewStatusText;
            });
        }, topDp: 12).SetGravity(GravityFlags.Center);

        FailureBlock(form, viewModel, includeAnotherOption: false);
    }

    /// <summary>
    /// One app as a settings card: its colourful mark, the name with what the account offers under it, and the switch;
    /// where its data lives as a drop-down under it, then a server address, a note or an error.
    /// </summary>
    private View AppRow(Context context, ViewBinder binder, AccountSetupPageViewModel viewModel, AccountSetupAppRow row)
    {
        int Dp(double value) => WinoUnits.Dp(context, value);
        var column = new LinearLayout(context) { Orientation = Orientation.Vertical };

        var card = new LinearLayout(context) { Orientation = Orientation.Vertical };
        var background = new GradientDrawable();
        background.SetCornerRadius(WinoUnits.DpF(context, 12));
        background.SetColor(WinoColors.CardFill(context));
        background.SetStroke(Dp(1), WinoColors.CardStroke(context));
        card.Background = background;
        card.SetPadding(Dp(16), Dp(12), Dp(12), Dp(12));
        column.AddView(card);

        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        header.SetMinimumHeight(Dp(48));
        header.SetGravity(GravityFlags.CenterVertical);
        card.AddView(header);
        var glyph = new WinoIconView(context) { Icon = AppGlyph(row.Kind), GlyphSize = 26, Colorful = true };
        header.AddView(glyph, new LinearLayout.LayoutParams(Dp(32), Dp(32)) { MarginEnd = Dp(14) });
        var texts = new LinearLayout(context) { Orientation = Orientation.Vertical };
        header.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var title = new TextView(context) { Text = row.Title };
        title.SetTextAppearance(Resource.Style.TextAppearance_Wino_ListTitle);
        texts.AddView(title);
        var status = new TextView(context);
        status.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
        texts.AddView(status);
        binder.Bind(row, nameof(row.StatusText), r => r.HasStatus ? r.StatusText : null, text => OnboardingViews.Caption(status, text));

        if (row.IsSwitchVisible)
        {
            var toggle = new MaterialSwitch(context) { ContentDescription = string.Format(Translator.AccountSetup_Review_UseApp, row.Title) };
            binder.Checked(toggle, row, nameof(row.IsOn), r => r.IsOn, (r, value) => r.IsOn = value);
            header.AddView(toggle, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = Dp(8) });
        }

        var sourceName = row.Kind == AccountSetupAppKind.Mail
            ? Translator.AccountSetup_Review_MailRange
            : string.Format(Translator.AccountSetup_Review_SourceOf, row.Title);
        if (LayoutInflater.From(context)?.Inflate(Resource.Layout.onboarding_dropdown, card, false) is TextInputLayout source &&
            source.EditText is MaterialAutoCompleteTextView field)
        {
            source.Hint = sourceName;
            field.ContentDescription = sourceName;
            field.SetSimpleItems(row.Options.Select(option => option.Label).ToArray());
            card.AddView(source);
            if (source.LayoutParameters is ViewGroup.MarginLayoutParams margins) margins.TopMargin = Dp(8);
            binder.Bind(row, nameof(row.SelectedOption), r => r.SelectedOption?.Label, label =>
            {
                if (field.Text != (label ?? string.Empty)) field.SetText(label ?? string.Empty, false);
            });
            binder.BindAny(row, r => r.IsSourceVisible, visible => OnboardingViews.Show(source, visible));
            EventHandler<AdapterView.ItemClickEventArgs> chosen = (_, args) =>
            {
                if (args.Position >= 0 && args.Position < row.Options.Count) row.SelectedOption = row.Options[args.Position];
            };
            field.ItemClick += chosen;
            binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => field.ItemClick -= chosen));
        }

        if (LayoutInflater.From(context)?.Inflate(Resource.Layout.onboarding_text_field, column, false) is TextInputLayout server &&
            server.EditText is { } serverEdit)
        {
            server.Hint = Translator.AccountSetup_ServerAddressHeader;
            server.PlaceholderText = Translator.AccountSetup_ServerAddressPlaceholder;
            server.HelperText = Translator.AccountSetup_ServerAddressDescription;
            serverEdit.InputType = InputTypes.ClassText | InputTypes.TextVariationUri;
            binder.TwoWayText(serverEdit, row, nameof(row.ServerUrl), r => r.ServerUrl, (r, value) => r.ServerUrl = value);
            SubmitOnEnter(serverEdit, binder, viewModel);
            column.AddView(server);
            binder.BindAny(row, r => r.IsServerEntryShown, visible => OnboardingViews.Show(server, visible));
        }

        var note = new TextView(context);
        note.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
        note.SetPadding(Dp(4), Dp(6), Dp(4), 0);
        column.AddView(note);
        binder.BindAny(row, r => r.IsNoteShown ? r.Note : null, text => OnboardingViews.Caption(note, text));

        var error = new TextView(context) { AccessibilityLiveRegion = AccessibilityLiveRegion.Assertive };
        error.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
        error.SetTextColor(WinoColors.Critical(context));
        error.SetPadding(Dp(4), Dp(6), Dp(4), 0);
        column.AddView(error);
        binder.BindAny(row, r => r.HasError ? r.Error : null, text => OnboardingViews.Caption(error, text));

        binder.BindAny(row, r => r.AutomationName, name => header.ContentDescription = name);
        return column;
    }

    private static WinoIconGlyph AppGlyph(AccountSetupAppKind kind) => kind switch
    {
        AccountSetupAppKind.Calendar => WinoIconGlyph.AppModeCalendar,
        AccountSetupAppKind.Contacts => WinoIconGlyph.AppModeContacts,
        AccountSetupAppKind.Tasks => WinoIconGlyph.AppModeToDo,
        _ => WinoIconGlyph.AppModeMail
    };

    private static void SetEnabled(View view, bool enabled)
    {
        view.Enabled = enabled;
        if (view is ViewGroup group)
            for (var index = 0; index < group.ChildCount; index++)
                if (group.GetChildAt(index) is { } child) SetEnabled(child, enabled);
    }

    /// <summary>The Windows Server details expander: a header that opens the incoming, outgoing, CalDAV and CardDAV lines and Edit servers.</summary>
    private static void ServerDetails(Form form, AccountSetupPageViewModel viewModel)
    {
        var context = form.Context;
        int Dp(double value) => WinoUnits.Dp(context, value);
        var header = new LinearLayout(context) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        header.SetMinimumHeight(Dp(48));
        header.SetGravity(GravityFlags.CenterVertical);
        header.Background = context.GetDrawable(global::Android.Resource.Drawable.ListSelectorBackground);
        var label = new TextView(context) { Text = Translator.AccountSetup_Review_ServerDetails };
        label.SetTextAppearance(Resource.Style.TextAppearance_Wino_SectionLabel);
        header.AddView(label, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        var chevron = new WinoIconView(context) { Icon = WinoIconGlyph.ChevronDown, GlyphSize = 16 };
        header.AddView(chevron, new LinearLayout.LayoutParams(Dp(24), Dp(24)));
        form.Add(header);
        if (header.LayoutParameters is ViewGroup.MarginLayoutParams headerMargins) headerMargins.TopMargin = Dp(8);

        var body = form.Group(() =>
        {
            Line(Translator.AccountSetup_Check_Incoming, vm => vm.ServerDetailsIncoming);
            Line(Translator.AccountSetup_Check_Outgoing, vm => vm.ServerDetailsOutgoing);
            Line(Translator.ProviderSelection_SourceCalDav, vm => vm.ServerDetailsCalDav);
            Line(Translator.ProviderSelection_SourceCardDav, vm => vm.ServerDetailsCardDav);
            form.Link(Translator.AccountSetup_Review_EditServers, viewModel.OpenAdvancedSetupCommand);
        });
        OnboardingViews.Show(body, false);
        void Toggle(bool expanded)
        {
            OnboardingViews.Show(body, expanded);
            chevron.Icon = expanded ? WinoIconGlyph.ChevronUp : WinoIconGlyph.ChevronDown;
            header.ContentDescription = $"{Translator.AccountSetup_Review_ServerDetails}, {(expanded ? Translator.WinoIntelligence_Expanded : Translator.WinoIntelligence_Collapsed)}";
        }
        Toggle(false);
        form.Binder.Click(header, () => Toggle(body.Visibility != ViewStates.Visible));

        void Line(string title, Func<AccountSetupPageViewModel, string> read)
        {
            form.Help(title, topDp: 8);
            var value = form.Help(topDp: 0);
            value.SetTextColor(WinoColors.PrimaryText(context));
            value.SetTextIsSelectable(true);
            form.Binder.BindAny(viewModel, read, text => value.Text = text);
        }
    }

    #endregion

    #region Progress

    private void BuildProgressStep(Form form, AccountSetupPageViewModel viewModel)
    {
        var context = form.Context;
        var steps = new LinearLayout(context) { Orientation = Orientation.Vertical, AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        form.Add(steps);
        // The step rows carry the page padding of the progress page; this column already has it.
        if (steps.LayoutParameters is LinearLayout.LayoutParams margins)
        {
            var padding = context.Resources?.GetDimensionPixelSize(Resource.Dimension.onboarding_page_padding) ?? 0;
            margins.Width = ViewGroup.LayoutParams.WrapContent;
            margins.Gravity = GravityFlags.CenterHorizontal;
            margins.MarginStart = -padding;
            margins.MarginEnd = -padding;
        }
        AccountSetupFragment.BindSteps(steps, form.Binder, viewModel.Steps);

        var added = form.InfoBar(WinoInfoBarSeverity.Success);
        form.Binder.BindAny(viewModel, vm => (vm.IsAddedVisible, vm.AddedMessage), state =>
        {
            added.Message = state.AddedMessage;
            added.IsOpen = state.IsAddedVisible;
            OnboardingViews.Show(added, state.IsAddedVisible);
        });

        var results = form.Group(() => { });
        form.Binder.BindAny(viewModel, vm => (vm.IsAddedVisible, vm.ResultRows), state =>
        {
            OnboardingViews.Show(results, state.IsAddedVisible);
            results.RemoveAllViews();
            foreach (var row in state.ResultRows)
                results.AddView(ResultRow(context, row), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                {
                    Gravity = GravityFlags.CenterHorizontal
                });
        });

        form.GroupWhen(vm => vm.IsSetupFailedVisible, () =>
        {
            var failed = form.InfoBar(WinoInfoBarSeverity.Error);
            failed.Message = Translator.AccountSetup_FailureMessage;
            form.Binder.BindAny(viewModel, vm => vm.IsSetupFailedVisible, open => failed.IsOpen = open);
            Actions(form, failed, flow =>
            {
                ActionButton(form, flow, Translator.AccountSetup_TryAgainButton, viewModel.TryAgainCommand, ButtonKind.Filled);
                ActionButton(form, flow, Translator.AccountSetup_GoBackButton, viewModel.GoBackFromFailureCommand, ButtonKind.Outlined);
                form.ShowWhen(ActionButton(form, flow, Translator.AccountSetup_ShowDetails, viewModel.ShowSetupFailureDetailsCommand, ButtonKind.Text), vm => vm.HasSetupFailureDetails);
            });
        });
    }

    /// <summary>One line of the result: the app's glyph, its name and where its data lives.</summary>
    private static View ResultRow(Context context, AccountSetupResultRow row)
    {
        int Dp(double value) => WinoUnits.Dp(context, value);
        var line = new LinearLayout(context) { Orientation = Orientation.Horizontal, ContentDescription = row.AutomationName };
        line.SetGravity(GravityFlags.CenterVertical);
        line.SetPadding(0, Dp(6), 0, Dp(6));
        var glyph = new WinoIconView(context) { Icon = row.Glyph, GlyphSize = 16 };
        line.AddView(glyph, new LinearLayout.LayoutParams(Dp(20), Dp(20)) { MarginEnd = Dp(10) });
        var title = new TextView(context) { Text = row.Title, ImportantForAccessibility = ImportantForAccessibility.No };
        title.SetTextAppearance(Resource.Style.TextAppearance_Wino_Body);
        title.SetTypeface(title.Typeface, global::Android.Graphics.TypefaceStyle.Bold);
        title.SetTextColor(WinoColors.PrimaryText(context));
        line.AddView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = Dp(10) });
        var detail = new TextView(context) { Text = row.Detail, ImportantForAccessibility = ImportantForAccessibility.No };
        detail.SetTextAppearance(Resource.Style.TextAppearance_Wino_Body);
        line.AddView(detail, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return line;
    }

    #endregion

    #region Pieces

    private static void CenterHorizontally(View view)
    {
        if (view.LayoutParameters is not LinearLayout.LayoutParams parameters) return;
        parameters.Gravity = GravityFlags.CenterHorizontal;
        view.LayoutParameters = parameters;
    }

    private enum ButtonKind { Filled, Outlined, Text }

    /// <summary>A wrapping row of buttons under an info bar (the Windows InfoBar content panel).</summary>
    /// <summary>A wrapping row of buttons inside <paramref name="bar"/>, under its text (Windows InfoBar.Content).</summary>
    private static void Actions(Form form, WinoInfoBar bar, Action<WinoFlowView> build)
    {
        var context = form.Context;
        var flow = new WinoFlowView(context) { Spacing = WinoUnits.Dp(context, 8), LineSpacing = WinoUnits.Dp(context, 8) };
        bar.Content = flow;
        build(flow);
    }

    private static MaterialButton ActionButton(Form form, WinoFlowView flow, string text, System.Windows.Input.ICommand? command, ButtonKind kind, Action? click = null)
    {
        var context = flow.Context ?? throw new InvalidOperationException("The flow has no context.");
        var button = kind switch
        {
            ButtonKind.Filled => new MaterialButton(context),
            ButtonKind.Outlined => new MaterialButton(context, null, Resource.Attribute.materialButtonOutlinedStyle),
            _ => new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle)
        };
        button.Text = text;
        button.SetAllCaps(false);
        button.SetMinHeight(WinoUnits.Dp(context, 40));
        button.InsetTop = 0;
        button.InsetBottom = 0;
        flow.AddView(button, new ViewGroup.MarginLayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        if (command is not null) form.Binder.Command(button, command);
        else if (click is not null) form.Binder.Click(button, click);
        return button;
    }

    /// <summary>A caption that follows a computed value and hides while it is empty.</summary>
    private static TextView CaptionWhen(Form form, Func<AccountSetupPageViewModel, string?> read, int topDp = 8)
    {
        var label = form.Help(topDp: topDp);
        form.Binder.BindAny(form.Source, read, text => OnboardingViews.Caption(label, text));
        return label;
    }

    #endregion
}
