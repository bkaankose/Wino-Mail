using Android.Content;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Address-first Add account (Windows AccountSetupPage, the shared <see cref="AccountSetupPageViewModel"/>): one column
/// with the step's title, the account pill with Change, the current step, then Back and the step's action. The steps
/// (address, discovery, sign-in method, sign-in, review, progress) are built once and follow <see cref="AccountSetupPageViewModel.Step"/>;
/// every rule stays in the ViewModel. The wizard centres the title and pill as the Windows welcome window does;
/// Settings starts them at the edge. Flyouts on Windows (colour, provider picker, Change menu, local account info) are
/// Material dialogs and a popup menu here.
/// </summary>
public sealed partial class AccountSetupPageFragment : WinoFragment<AccountSetupPageViewModel>
{
    protected override int LayoutResource => Resource.Layout.fragment_account_setup_page;

    protected override void OnBind(View view, AccountSetupPageViewModel viewModel, IDispatcher dispatcher)
    {
#if DEBUG
        global::Wino.Mail.Android.Views.Debug.DebugOnboarding.Track(viewModel);
#endif
        var binder = new ViewBinder(Bindings, dispatcher, ReportError);
        WinoInsets.ApplyWithKeyboard(view.FindViewById(Resource.Id.onboarding_insets) ?? view);
        var context = view.RequireContext();

        OnboardingViews.Toolbar(view, binder, () => GoBack(viewModel))?.SetHeading(Translator.WelcomeWizard_Step2Title);
        // System back walks the steps; on the Settings address step it leaves the page through the Settings stack.
        OnboardingViews.InterceptBackWhen(this, binder, viewModel,
            vm => vm.IsBackVisible || vm.Step == AccountSetupStep.Progress, () => GoBack(viewModel));

        BindHeader(view, binder, viewModel);
        BindKnownRow(view, context, binder, viewModel);
        BindFooterLinks(view, context, binder, viewModel);
        BindActions(view, context, binder, viewModel);

        if (view.FindViewById<LinearLayout>(Resource.Id.setup_page_step) is { } host)
        {
            var form = new OnboardingForm<AccountSetupPageViewModel>(context, host, binder, viewModel);
            form.GroupWhen(vm => vm.Step == AccountSetupStep.Address, () => BuildAddressStep(form, viewModel));
            form.GroupWhen(vm => vm.Step == AccountSetupStep.Discovery, () => BuildDiscoveryStep(form, viewModel));
            form.GroupWhen(vm => vm.Step == AccountSetupStep.SignInMethod, () => BuildSignInMethodStep(form, viewModel));
            form.GroupWhen(vm => vm.Step == AccountSetupStep.SignIn, () => BuildSignInStep(form, viewModel));
            form.GroupWhen(vm => vm.Step == AccountSetupStep.Review, () => BuildReviewStep(form, viewModel));
            form.GroupWhen(vm => vm.Step == AccountSetupStep.Progress, () => BuildProgressStep(form, viewModel));
        }

        if (view.FindViewById<AndroidX.Core.Widget.NestedScrollView>(Resource.Id.setup_page_scroll) is { } scroll)
            binder.BindAny(viewModel, vm => vm.Step, _ =>
            {
                scroll.ScrollTo(0, 0);
                // A new step starts without the previous step's keyboard; hidden fields hand focus on, so wait for layout.
                scroll.Post(() =>
                {
                    scroll.FindFocus()?.ClearFocus();
                    (context.GetSystemService(Context.InputMethodService) as InputMethodManager)?.HideSoftInputFromWindow(scroll.WindowToken, HideSoftInputFlags.None);
                });
            });
    }

    /// <summary>
    /// The toolbar arrow and system back: the ViewModel's Back while the step has one, Go back or Done after setup ended,
    /// nothing while the account is being added, and the host's back on the Settings address step.
    /// </summary>
    private void GoBack(AccountSetupPageViewModel viewModel)
    {
        if (viewModel.Step == AccountSetupStep.Progress)
        {
            if (viewModel.Phase == AccountSetupPhase.Failed) Run(viewModel.GoBackFromFailureCommand);
            else if (viewModel.Phase == AccountSetupPhase.Added) Run(viewModel.PrimaryCommand);
            return;
        }

        if (viewModel.IsBackVisible)
        {
            Run(viewModel.BackCommand);
            return;
        }

        MainApplication.Services?.GetService<INavigationService>()?.GoBack();
    }

    private void BindHeader(View view, ViewBinder binder, AccountSetupPageViewModel viewModel)
    {
        var logo = view.FindViewById<ImageView>(Resource.Id.setup_page_logo);
        var title = view.FindViewById<TextView>(Resource.Id.setup_page_title);
        var subtitle = view.FindViewById<TextView>(Resource.Id.setup_page_subtitle);

        // The wizard centres the title like the Windows welcome window; Settings starts it at the edge.
        var knownRow = view.FindViewById(Resource.Id.setup_page_known_row);
        var links = view.FindViewById(Resource.Id.setup_page_links);
        binder.BindAny(viewModel, vm => vm.IsSettingsHost, isSettings =>
        {
            var gravity = isSettings ? GravityFlags.Start : GravityFlags.CenterHorizontal;
            if (title is not null) title.Gravity = gravity;
            if (subtitle is not null) subtitle.Gravity = gravity;
            foreach (var aligned in new[] { knownRow, links })
            {
                if (aligned?.LayoutParameters is not LinearLayout.LayoutParams parameters || parameters.Gravity == gravity) continue;
                parameters.Gravity = gravity;
                aligned.LayoutParameters = parameters;
            }
        });
        binder.BindAny(viewModel, vm => vm.Step == AccountSetupStep.Address, visible => OnboardingViews.Show(logo, visible));
        binder.BindAny(viewModel, vm => vm.HasStepTitle ? vm.StepTitle : null, text => OnboardingViews.Caption(title, text));
        binder.BindAny(viewModel, vm => vm.HasStepSubtitle ? vm.StepSubtitle : null, text => OnboardingViews.Caption(subtitle, text));
    }

    #region Account pill

    private void BindKnownRow(View view, Context context, ViewBinder binder, AccountSetupPageViewModel viewModel)
    {
        if (view.FindViewById(Resource.Id.setup_page_known_row) is { } row)
            binder.BindAny(viewModel, vm => vm.IsKnownCardVisible, visible => OnboardingViews.Show(row, visible));

        var card = view.FindViewById<LinearLayout>(Resource.Id.setup_page_known_card);
        if (card is not null)
        {
            var background = new GradientDrawable();
            background.SetCornerRadius(WinoUnits.DpF(context, 24));
            background.SetColor(WinoColors.CardFill(context));
            background.SetStroke(WinoUnits.Dp(context, 1), WinoColors.CardStroke(context));
            card.Background = background;
        }

        var icon = view.FindViewById<WinoIconView>(Resource.Id.setup_page_known_icon);
        var identity = view.FindViewById<TextView>(Resource.Id.setup_page_known_identity);
        var items = view.FindViewById<LinearLayout>(Resource.Id.setup_page_known_items);
        var flow = new WinoFlowView(context) { Spacing = WinoUnits.Dp(context, 6), LineSpacing = WinoUnits.Dp(context, 2) };
        items?.AddView(flow, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // KnownText changes whenever the items are rebuilt; the colour and the route decide the circle.
        binder.BindAny(viewModel, vm => (vm.KnownText, vm.SelectedColor?.Hex, vm.RouteProviderType, vm.RouteSpecialImapProvider, vm.IsLocalOnlyMode, vm.RouteEvidenceText), _ =>
        {
            var identityText = string.Empty;
            var reason = string.Empty;
            var hasProvider = false;
            flow.RemoveAllViews();
            foreach (var item in viewModel.KnownItems.ToList())
            {
                switch (item.Kind)
                {
                    case AccountSetupKnownItemKind.Identity:
                        identityText = item.Text;
                        continue;
                    case AccountSetupKnownItemKind.Reason:
                        reason = item.Text;
                        continue;
                }
                hasProvider |= item.Kind == AccountSetupKnownItemKind.Provider;
                flow.AddView(KnownChip(context, item, separator: flow.ChildCount > 0));
            }
            if (identity is not null) identity.Text = identityText;
            OnboardingViews.Show(flow, flow.ChildCount > 0);

            var providerGlyph = hasProvider ? MailProviderGlyphs.GetGlyph(viewModel.RouteProviderType, viewModel.RouteSpecialImapProvider) : WinoIconGlyph.Mail;
            var glyph = viewModel.IsLocalOnlyMode ? WinoIconGlyph.Laptop : providerGlyph == WinoIconGlyph.None ? WinoIconGlyph.Mail : providerGlyph;
            if (icon is not null) ApplyCircleGlyph(context, icon, glyph, viewModel.SelectedColor?.Hex);

            if (card is not null)
            {
                card.ContentDescription = string.IsNullOrWhiteSpace(viewModel.RouteEvidenceText)
                    ? viewModel.KnownText
                    : $"{viewModel.KnownText}. {viewModel.RouteEvidenceText}";
                // Why Wino chose the provider ("Found a mail server at…", "Chosen by you"), the Windows pill tooltip.
                TooltipCompat.SetTooltipText(card, reason.Length > 0 ? reason : null);
            }
        });

        if (view.FindViewById<CircularProgressIndicator>(Resource.Id.setup_page_known_progress) is { } progress)
        {
            progress.IndicatorSize = WinoUnits.Dp(context, 18);
            binder.BindAny(viewModel, vm => (vm.IsResolvingVisible, vm.DiscoveryText), state =>
            {
                OnboardingViews.Show(progress, state.IsResolvingVisible);
                progress.ContentDescription = state.DiscoveryText;
            });
        }

        if (view.FindViewById<MaterialButton>(Resource.Id.setup_page_change) is { } change)
        {
            change.Text = Translator.AccountSetup_Change;
            binder.BindAny(viewModel, vm => vm.IsChangeAddressVisible, visible => OnboardingViews.Show(change, visible));
            binder.Click(change, () => ShowChangeMenu(change, viewModel));
        }
    }

    /// <summary>One item of the pill's capability line: "·" between items, a success check before a sign-in.</summary>
    private static View KnownChip(Context context, AccountSetupKnownItem item, bool separator)
    {
        var line = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        line.SetGravity(GravityFlags.CenterVertical);
        if (separator)
        {
            var dot = new TextView(context) { Text = "·" };
            dot.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
            dot.SetTextColor(WinoColors.TertiaryText(context));
            line.AddView(dot, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = WinoUnits.Dp(context, 6) });
        }
        if (item.Kind == AccountSetupKnownItemKind.SignIn)
        {
            var check = new WinoIconView(context) { Icon = WinoIconGlyph.Checkmark, GlyphSize = 14, Colorful = false };
            check.SetTextColor(WinoColors.Success(context));
            line.AddView(check, new LinearLayout.LayoutParams(WinoUnits.Dp(context, 16), WinoUnits.Dp(context, 16)) { MarginEnd = WinoUnits.Dp(context, 4) });
        }
        var text = new TextView(context) { Text = item.Text };
        text.SetTextAppearance(Resource.Style.TextAppearance_Wino_Caption);
        if (!string.IsNullOrEmpty(item.HelpText)) TooltipCompat.SetTooltipText(text, item.HelpText);
        line.AddView(text);
        line.LayoutParameters = new ViewGroup.MarginLayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        return line;
    }

    /// <summary>A glyph on a round fill: the account colour with black or white on it, otherwise the subtle fill.</summary>
    private static void ApplyCircleGlyph(Context context, WinoIconView icon, WinoIconGlyph glyph, string? hex)
    {
        var fill = WinoColors.FromHexString(hex);
        var circle = new GradientDrawable();
        circle.SetShape(ShapeType.Oval);
        circle.SetColor(fill ?? WinoColors.SubtleFill(context));
        icon.Background = circle;
        icon.GlyphSize = 16;
        icon.Colorful = fill is null ? null : false;
        icon.Icon = glyph;
        icon.SetTextColor(fill is { } color ? WinoColors.ReadableText(color) : WinoColors.PrimaryText(context));
    }

    /// <summary>Change opens a menu; "Change provider" then opens the provider picker.</summary>
    private void ShowChangeMenu(View anchor, AccountSetupPageViewModel viewModel)
    {
        if (anchor.Context is not { } context) return;
        var menu = new PopupMenu(context, anchor);
        var address = menu.Menu.Add(0, 1, 0, Translator.AccountSetup_ChangeAddress);
        var provider = viewModel.IsChangeProviderVisible ? menu.Menu.Add(0, 2, 1, Translator.AccountSetup_ChangeProvider) : null;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            address?.SetContentDescription(viewModel.ChangeAddressAutomationName);
            provider?.SetContentDescription(viewModel.ChangeProviderAutomationName);
        }
        menu.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId == 1) Run(viewModel.ChangeAddressCommand);
            else if (args.Item?.ItemId == 2) ShowProviderPicker(viewModel);
        };
        menu.Show();
    }

    #endregion

    private void BindFooterLinks(View view, Context context, ViewBinder binder, AccountSetupPageViewModel viewModel)
    {
        if (view.FindViewById(Resource.Id.setup_page_links) is { } links)
            binder.BindAny(viewModel, vm => vm.IsFooterVisible, visible => OnboardingViews.Show(links, visible));
        if (view.FindViewById<MaterialButton>(Resource.Id.setup_page_advanced) is { } advanced)
        {
            advanced.Text = Translator.AccountSetup_AdvancedSetup;
            binder.Command(advanced, viewModel.OpenAdvancedSetupCommand);
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.setup_page_local) is { } local)
        {
            local.Text = Translator.AccountSetup_LocalAccountSetup;
            binder.Command(local, viewModel.StartLocalOnlyCommand);
        }
        if (view.FindViewById<WinoIconView>(Resource.Id.setup_page_local_info) is { } info)
        {
            info.Icon = WinoIconGlyph.Info;
            info.GlyphSize = 16;
            info.ContentDescription = Translator.AccountSetup_LocalAccountSetup;
            TooltipCompat.SetTooltipText(info, Translator.AccountSetup_LocalAccountInfo);
            binder.Click(info, () => new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(context)
                .SetTitle(Translator.AccountSetup_LocalAccountSetup)!
                .SetMessage(Translator.AccountSetup_LocalAccountInfo)!
                .SetPositiveButton(Translator.Buttons_OK, (IDialogInterfaceOnClickListener?)null)!
                .Show());
        }
    }

    private void BindActions(View view, Context context, ViewBinder binder, AccountSetupPageViewModel viewModel)
    {
        if (view.FindViewById<MaterialButton>(Resource.Id.setup_page_back) is { } back)
        {
            back.Text = Translator.AccountSetup_Back;
            binder.Command(back, viewModel.BackCommand);
            binder.BindAny(viewModel, vm => vm.IsBackVisible, visible => back.Visibility = visible ? ViewStates.Visible : ViewStates.Invisible);
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.setup_page_cancel_discovery) is { } cancel)
        {
            cancel.Text = Translator.AccountSetup_SetUpManually;
            cancel.SetAllCaps(false);
            binder.Command(cancel, viewModel.CancelDiscoveryToAdvancedCommand);
            binder.BindAny(viewModel, vm => vm.IsResolvingVisible, visible => OnboardingViews.Show(cancel, visible));
        }
        if (view.FindViewById<FrameLayout>(Resource.Id.setup_page_social_host) is { } socialHost)
        {
            var social = ProviderButton(context);
            socialHost.AddView(social, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
            binder.Command(social, viewModel.SignInWithProviderCommand);
            binder.BindAny(viewModel, vm => (vm.IsFooterSocialSignInVisible, vm.NativeSignInButtonText, vm.RouteProviderType, vm.RouteSpecialImapProvider), state =>
            {
                OnboardingViews.Show(socialHost, state.IsFooterSocialSignInVisible);
                social.Text = state.NativeSignInButtonText;
                social.ContentDescription = state.NativeSignInButtonText;
                social.Icon = WinoIcons.Drawable(context, MailProviderGlyphs.GetGlyph(state.RouteProviderType, state.RouteSpecialImapProvider), 18, colorful: true);
                social.IconTint = null;
            });
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.setup_page_primary) is { } primary)
        {
            binder.Command(primary, viewModel.PrimaryCommand);
            binder.BindAny(viewModel, vm => (vm.IsPrimaryVisible, vm.PrimaryText), state =>
            {
                OnboardingViews.Show(primary, state.IsPrimaryVisible);
                primary.Text = state.PrimaryText;
            });
        }
    }

    /// <summary>An outlined pill with a colourful provider mark (the Windows SocialSignInButtonStyle).</summary>
    private static MaterialButton ProviderButton(Context context)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.materialButtonOutlinedStyle)
        {
            IconGravity = MaterialButton.IconGravityTextStart,
            IconPadding = WinoUnits.Dp(context, 8)
        };
        button.SetAllCaps(false);
        button.SetMinHeight(WinoUnits.Dp(context, 40));
        button.SetTextColor(WinoColors.PrimaryText(context));
        button.StrokeColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.Outline(context));
        return button;
    }

    /// <summary>Enter (or the keyboard's Go) runs the step's primary action, as Enter does in the Windows fields.</summary>
    private void SubmitOnEnter(EditText? edit, ViewBinder binder, AccountSetupPageViewModel viewModel)
    {
        if (edit is null) return;
        edit.ImeOptions = ImeAction.Go;
        EventHandler<TextView.EditorActionEventArgs> handler = (_, args) =>
        {
            var isEnter = args.ActionId == ImeAction.Go ||
                          (args.Event is { KeyCode: Keycode.Enter, Action: KeyEventActions.Down });
            if (!isEnter)
            {
                args.Handled = false;
                return;
            }
            args.Handled = true;
            if (viewModel.IsPrimaryVisible) Run(viewModel.PrimaryCommand);
        };
        edit.EditorAction += handler;
        binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => edit.EditorAction -= handler));
    }

    private void Run(System.Windows.Input.ICommand command, object? parameter = null)
    {
        try { if (command.CanExecute(parameter)) command.Execute(parameter); }
        catch (Exception exception) { ReportError(exception); }
    }
}
