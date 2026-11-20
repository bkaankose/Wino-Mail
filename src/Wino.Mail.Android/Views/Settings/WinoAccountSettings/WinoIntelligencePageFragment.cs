using System.Windows.Input;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using Wino.Mail.ViewModels.Common;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.WinoAccountSettings;

/// <summary>
/// Wino Intelligence home (Windows WinoIntelligencePage, Mac WinoIntelligencePageViewController; shared
/// WinoAccountManagementPageViewModel). The page is always listed, so it also explains the subscription. Without access:
/// the hero with the one call to action, "What's included" and the locked features. With access: the status (plan and
/// this month's usage), Wino Assistant where the platform has voice control, Smart Labels and Daily Briefing, the
/// languages, and the Privacy section with AI provider access. Android v1 sells nothing in the app
/// (IPlatformCapabilities.Purchases is false), so the hero offers Sign in and Learn more instead of checkout.
/// </summary>
public sealed class WinoIntelligencePageFragment : SettingsPageFragment<WinoAccountManagementPageViewModel>
{
    private IMailDialogService? _dialogs;
    private bool _sellsAddOns;
    private WinoTextSwitch? _consent;
    private LinearLayout? _content;
    private View? _loading;
    private bool _applyingConsent;
    private bool _revealed;

    protected override void BuildPage()
    {
        var vm = VM;
        _dialogs = MainApplication.Services?.GetService<IMailDialogService>();
        _sellsAddOns = vm.IsPurchaseAvailable;
        _revealed = false;
        _applyingConsent = false;

        // First load: the page starts with default ViewModel state (signed out, no access). Showing it would flash the
        // offer before the cached account state lands, so the content stays hidden behind one spinner until then.
        _loading = Add(IntelligenceViews.LoadingState(Ctx));
        var content = Stack(0);
        content.Visibility = ViewStates.Gone;
        _content = content;
        Add(content);
        Bindings.Own(new ActionDisposable(() =>
        {
            _content = null;
            _loading = null;
            _consent = null;
        }));

        // The network refresh row appears only when the refresh is slow, so a quick refresh does not push the cards
        // down and back up.
        var spinner = IntelligenceViews.Spinner(Ctx);
        var updating = Row(spinner, Text(Translator.WinoIntelligence_Updating));
        var showUpdating = IntelligenceViews.DelayedProgressRow(updating, spinner, () => !Bindings.IsDisposed);
        Bind.Bind(vm, nameof(vm.IsIntelligenceRefreshing), s => s.IsIntelligenceRefreshing, on =>
        {
            // The refresh starts after the account state and the cached snapshot are applied.
            if (on) Reveal();
            showUpdating(on);
        });
        AddInset(content, updating);
        AddInset(content, CommandBar(WinoInfoBarSeverity.Informational, nameof(vm.PurchaseStatusMessage), s => s.PurchaseStatusMessage,
            vm.RefreshPurchasesText, vm.RefreshPurchasesCommand));
        var refreshError = CommandBar(WinoInfoBarSeverity.Warning, nameof(vm.IntelligenceRefreshError), s => s.IntelligenceRefreshError,
            Translator.Buttons_Retry, vm.RetryIntelligenceRefreshCommand);
        refreshError.IsClosable = true;
        AddInset(content, refreshError);
        AddInset(content, Bar(WinoInfoBarSeverity.Warning, nameof(vm.SubscriptionEndedText), s => s.SubscriptionEndedText));

        AddTo(content, Bind.Visible(OfferPanel(), vm, nameof(vm.HasIntelligenceAccess), s => s.HasNoIntelligenceAccess), 0);
        AddTo(content, Bind.Visible(SubscribedPanel(), vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess), 0);

        _ = RevealWhenLoadedAsync(vm);
    }

    /// <summary>The route's initializer (iOS InitializeAsync override).</summary>
    internal static Task InitializeAsync(WinoAccountManagementPageViewModel viewModel, NavigationMode mode, object? parameter)
        => PageLoadTracker.TrackAsync(viewModel, () => viewModel.InitializeAsync(mode, parameter));

    private async Task RevealWhenLoadedAsync(WinoAccountManagementPageViewModel vm)
    {
        var scope = Bindings;
        try { await PageLoadTracker.WhenLoaded(vm); }
        catch (Exception exception) { ReportError(exception); }
        await Dispatcher.ExecuteOnUIThread(() => { if (!scope.IsDisposed) Reveal(); });
    }

    /// <summary>Replaces the first-load spinner with the page content, once per view.</summary>
    private void Reveal()
    {
        if (_revealed || Bindings.IsDisposed) return;
        _revealed = true;
        if (_loading is not null) _loading.Visibility = ViewStates.Gone;
        if (_content is not null) _content.Visibility = ViewStates.Visible;
    }

    /// <summary>Appends notices and other text content with the section header's inset (a gone view takes no room).</summary>
    private void AddInset(LinearLayout stack, View view)
    {
        int inset = WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_header_start);
        stack.AddView(view, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            MarginStart = inset,
            MarginEnd = inset,
            TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_section_gap)
        });
    }

    /// <summary>A bespoke panel placed like a settings section: the section gap above it, edge to edge with the cards.</summary>
    private void AddPanel(LinearLayout stack, View view)
        => stack.AddView(view, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = WinoTokens.Px(Ctx, Resource.Dimension.wino_m3_settings_section_gap)
        });

    private WinoInfoBar Bar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message)
    {
        var bar = InfoBar(severity, null, null);
        Bind.Bind(VM, property, message, text =>
        {
            bar.Message = text;
            bar.Visibility = string.IsNullOrWhiteSpace(text) ? ViewStates.Gone : ViewStates.Visible;
        });
        return bar;
    }

    private WinoInfoBar CommandBar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message, string action, ICommand command)
    {
        var bar = Bar(severity, property, message);
        bar.ActionTitle = action;
        EventHandler handler = (_, _) =>
        {
            try { if (command.CanExecute(null)) command.Execute(null); }
            catch (Exception exception) { ReportError(exception); }
        };
        bar.ActionInvoked += handler;
        Bindings.Own(new ActionDisposable(() => bar.ActionInvoked -= handler));
        return bar;
    }

    private MaterialButton LearnMoreButton()
    {
        var learn = IntelligenceViews.LinkButton(Ctx, Translator.Buttons_LearnMore);
        Bind.Views.Click(learn, () => IntelligenceViews.OpenUrl(WinoAccountManagementPageFragment.IntelligenceLearnMoreUrl));
        return learn;
    }

    #region Offer

    private LinearLayout OfferPanel()
    {
        var panel = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        AddPanel(panel, Hero());
        AddPanel(panel, IncludedSurface());
        AddPanel(panel, LockedGroup());
        return panel;
    }

    /// <summary>
    /// The illustration and pitch on the brand wash with the one call to action (Windows WinoIntelligenceHero). Signed
    /// out: Get Wino Intelligence (or Sign in where the platform sells nothing) and Create account. Signed in: Subscribe
    /// where the platform sells it. Learn more where the platform sells nothing, or signed in.
    /// </summary>
    private View Hero()
    {
        var vm = VM;
        var context = Ctx;
        var art = new WinoIntelligencePromotionIllustrationView(context, Resource.Dimension.wino_winoaccount_promo_art_small);
        // The Windows title and text name Wino Assistant, which needs voice control.
        var title = Text(vm.IsVoiceControlAvailable ? Translator.WinoIntelligence_Hero_Title : Translator.WinoIntelligence_PromotionTitle, WinoTextStyle.Subtitle);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) title.AccessibilityHeading = true;
        var description = Text(vm.IsVoiceControlAvailable ? Translator.WinoIntelligence_Hero_Description : Translator.WinoIntelligence_Hero_Description_NoAssistant, WinoTextStyle.Description);
        var pitch = Stack(Resource.Dimension.wino_space_2xs, title, description);
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.AddView(art);
        row.AddView(pitch, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_l) });

        var signedOutButtons = new List<View>
        {
            _sellsAddOns
                ? Bind.Button(Translator.WinoIntelligence_GetButton, vm.GetWinoIntelligenceCommand, primary: true)
                : Bind.Button(Translator.Buttons_SignIn, vm.SignInCommand, primary: true),
            Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand)
        };
        if (!_sellsAddOns) signedOutButtons.Add(LearnMoreButton());
        var signedOut = Bind.Visible(IntelligenceViews.Flow(context, signedOutButtons, Resource.Dimension.wino_space_s), vm, nameof(vm.IsSignedIn), s => s.IsSignedOut);

        var signedInButtons = new List<View>();
        if (_sellsAddOns)
        {
            // Stripe Checkout states the price in the customer's own currency; the app does not repeat it.
            var subscribe = Bind.Button(vm.HeroSubscribeButtonText, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
            Bind.Bind(vm, nameof(vm.HeroSubscribeButtonText), s => s.HeroSubscribeButtonText, text => subscribe.Text = text);
            signedInButtons.Add(subscribe);
        }
        signedInButtons.Add(LearnMoreButton());
        var signedIn = Bind.Visible(IntelligenceViews.Flow(context, signedInButtons, Resource.Dimension.wino_space_s), vm, nameof(vm.IsSignedIn), s => s.IsSignedIn);

        var column = Stack(Resource.Dimension.wino_space_l, row, signedOut, signedIn);
        if (_sellsAddOns)
        {
            var addOn = vm.AiPackAddOn;
            var error = Bind.Label(addOn, nameof(addOn.ErrorText), a => a.ErrorText, WinoTextStyle.Caption, maximumLines: 0);
            error.SetTextColor(WinoColors.Caution(context));
            Bind.Visible(error, addOn, nameof(addOn.ShowErrorState), a => a.ShowErrorState);
            AddTo(column, error, Resource.Dimension.wino_space_xs);
            AddTo(column, Bind.Label(vm, nameof(vm.IntelligenceHeroTermsText), s => s.IntelligenceHeroTermsText, WinoTextStyle.Caption, maximumLines: 0), Resource.Dimension.wino_space_xs);
        }
        else
        {
            // The checkout terms do not apply where the app sells nothing; the account requirement does.
            AddTo(column, Text(Translator.WinoIntelligence_AccountRequired, WinoTextStyle.Caption), Resource.Dimension.wino_space_xs);
        }

        var wash = WinoTokens.Color(context, Resource.Color.wino_winoaccount_brand_wash);
        var surface = new FrameLayout(context) { Background = IntelligenceViews.Shape(context, wash, WinoColors.CardStroke(context), Resource.Dimension.wino_radius_small) };
        WinoTokens.Pad(surface, Resource.Dimension.wino_space_xl, Resource.Dimension.wino_space_xl, Resource.Dimension.wino_space_xl, Resource.Dimension.wino_space_xl);
        surface.AddView(column, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return surface;
    }

    /// <summary>An icon, a title, a description and an optional caption (an allowance), with an optional trailing view.</summary>
    private LinearLayout FeatureRow(WinoIconGlyph glyph, string title, string description, string? caption, View? trailing = null)
    {
        var icon = new WinoIconView(Ctx) { Icon = glyph, GlyphSize = WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_l) };
        icon.SetTextColor(WinoColors.PrimaryText(Ctx));
        icon.ImportantForAccessibility = ImportantForAccessibility.No;
        var text = Stack(Resource.Dimension.wino_space_hairline, Text(title), Text(description, WinoTextStyle.Description));
        if (!string.IsNullOrWhiteSpace(caption)) AddTo(text, Text(caption, WinoTextStyle.Caption), Resource.Dimension.wino_space_2xs);
        var row = trailing is null
            ? IntelligenceViews.HStack(Ctx, Resource.Dimension.wino_space_l, text, icon, text)
            : IntelligenceViews.HStack(Ctx, Resource.Dimension.wino_space_l, text, icon, text, trailing);
        row.SetGravity(GravityFlags.Top);
        return row;
    }

    /// <summary>Appends a hairline divider, keeping its fixed height (<c>AddTo</c> would make it wrap content and fill).</summary>
    private void AddSeparator(LinearLayout stack, int spacing)
    {
        var line = IntelligenceViews.Separator(Ctx);
        var layout = (LinearLayout.LayoutParams)line.LayoutParameters!;
        if (stack.ChildCount > 0 && spacing != 0) layout.TopMargin = WinoTokens.Px(Ctx, spacing);
        stack.AddView(line, layout);
    }

    private void AddRows(LinearLayout stack, IEnumerable<View> rows)
    {
        foreach (var row in rows)
        {
            if (stack.ChildCount > 0) AddSeparator(stack, Resource.Dimension.wino_space_m);
            AddTo(stack, row, stack.ChildCount > 0 ? Resource.Dimension.wino_space_m : 0);
        }
    }

    /// <summary>"What's included": Wino Assistant first where the platform has voice control, then one row per perk with its allowance.</summary>
    private View IncludedSurface()
    {
        var vm = VM;
        var heading = Text(Translator.WinoIntelligence_Included_Title, WinoTextStyle.BodyStrong);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) heading.AccessibilityHeading = true;
        var headerText = Stack(Resource.Dimension.wino_space_hairline, heading, Text(Translator.WinoIntelligence_Included_Description, WinoTextStyle.Description));
        var header = IntelligenceViews.HStack(Ctx, Resource.Dimension.wino_space_l, headerText,
            new BrandIconView(Ctx, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l), headerText);
        var rows = Stack(0);
        void Rebuild()
        {
            rows.RemoveAllViews();
            var views = new List<View>();
            if (vm.IsVoiceControlAvailable)
            {
                var badge = IntelligenceViews.Pill(Ctx, Translator.WinoIntelligence_NewBadge, WinoColors.OnAccent(Ctx), WinoColors.Accent(Ctx));
                views.Add(FeatureRow(WinoIconGlyph.Microphone, Translator.WinoIntelligence_Perk_Voice_Title, Translator.WinoIntelligence_Perk_Voice_Description,
                    Translator.WinoIntelligence_Perk_Voice_Allowance, badge));
            }
            views.AddRange(vm.OfferPerks.Select(perk => (View)FeatureRow(perk.Icon, perk.Title, perk.Description, perk.TrailingText)));
            AddRows(rows, views);
        }
        Bind.Collection(vm.OfferPerks, Rebuild);
        Rebuild();
        var column = Stack(0, header);
        AddSeparator(column, Resource.Dimension.wino_space_l);
        AddTo(column, rows, Resource.Dimension.wino_space_m);
        return IntelligenceViews.Surface(Ctx, column, Resource.Dimension.wino_space_l, Resource.Dimension.wino_radius_small);
    }

    /// <summary>"Available with Wino Intelligence": Smart Labels and Daily Briefing and the languages, dimmed with a lock.</summary>
    private View LockedGroup()
    {
        View Lock()
        {
            var glyph = new WinoIconView(Ctx) { Icon = WinoIconGlyph.LockClosed, GlyphSize = WinoTokens.Dp(Ctx, Resource.Dimension.wino_glyph_m) };
            glyph.SetTextColor(WinoColors.SecondaryText(Ctx));
            glyph.ImportantForAccessibility = ImportantForAccessibility.No;
            return glyph;
        }
        var rows = Stack(0);
        AddRows(rows,
        [
            FeatureRow(WinoIconGlyph.Mail, Translator.WinoIntelligence_MailClassification_Title, Translator.WinoIntelligence_MailClassification_Description, null, Lock()),
            FeatureRow(WinoIconGlyph.Translate, Translator.WinoIntelligence_Languages_Title, Translator.WinoIntelligence_Languages_Description, null, Lock()),
        ]);
        rows.Alpha = WinoColors.Fraction(Ctx, Resource.Fraction.wino_m3_settings_disabled_alpha);
        rows.Enabled = false;
        var title = WinoTokens.Style(new TextView(Ctx) { Text = Translator.WinoIntelligence_Locked_Caption }, WinoTextStyle.SettingsSectionHeader);
        WinoTokens.Pad(title, Resource.Dimension.wino_m3_settings_header_start, 0, Resource.Dimension.wino_m3_settings_header_start, Resource.Dimension.wino_m3_settings_header_bottom);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(title, true);
        var stack = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        stack.AddView(title);
        stack.AddView(IntelligenceViews.Surface(Ctx, rows, Resource.Dimension.wino_space_l, Resource.Dimension.wino_radius_small),
            new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return stack;
    }

    #endregion

    #region Subscribed

    private LinearLayout SubscribedPanel()
    {
        var vm = VM;
        var panel = new LinearLayout(Ctx) { Orientation = Orientation.Vertical };
        AddPanel(panel, StatusSurface());

        var assistant = CommandCard(Translator.WinoIntelligence_Voice_Title, vm.AssistantHomeDescription, WinoIconGlyph.None, vm.OpenVoiceControlCommand);
        assistant.LeadingView = new BrandIconView(Ctx, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l);
        Bind.Bind(vm, nameof(vm.AssistantHomeDescription), s => s.AssistantHomeDescription, text => assistant.Description = text);
        Bind.Visible(assistant, vm, nameof(vm.ShowVoiceCommandsCard), s => s.ShowVoiceCommandsCard);
        var classification = CommandCard(Translator.WinoIntelligence_MailClassification_Title, Translator.WinoIntelligence_MailClassification_HomeDescription,
            WinoIconGlyph.Tag, vm.OpenMailClassificationCommand);
        classification.ShowsChevron = true;
        panel.AddView(Group(null, assistant, classification, LanguagesExpander()));

        panel.AddView(PrivacyGroup());
        return panel;
    }

    /// <summary>
    /// The status panel (Windows status expander, open on a phone): the brand tile, Wino Intelligence, the renewal line
    /// and Active or Limit reached; then the allowance notice, the plan row with Manage subscription and Refresh, and one
    /// meter per usage bucket.
    /// </summary>
    private View StatusSurface()
    {
        var vm = VM;
        var context = Ctx;
        var active = IntelligenceViews.SuccessPill(context, Translator.WinoAccount_Management_ActiveShort);
        Bind.Visible(active, vm, nameof(vm.IsWithinLimits), s => s.IsWithinLimits);
        var limitReached = IntelligenceViews.NeutralPill(context, Translator.WinoIntelligence_Status_LimitReached);
        Bind.Visible(limitReached, vm, nameof(vm.IsLimitReached), s => s.IsLimitReached);
        var title = Text(Translator.WinoIntelligence_SettingsTitle, WinoTextStyle.Subtitle);
        if (OperatingSystem.IsAndroidVersionAtLeast(28)) title.AccessibilityHeading = true;
        var renewal = Bind.Label(vm, nameof(vm.SubscriptionRenewalLine), s => s.SubscriptionRenewalLine, WinoTextStyle.Description, maximumLines: 0);
        var text = Stack(Resource.Dimension.wino_space_hairline, title, renewal);
        var tile = new FrameLayout(context)
        {
            Background = IntelligenceViews.Shape(context, WinoColors.SettingsGroupFill(context), WinoColors.CardStroke(context), Resource.Dimension.wino_radius_small)
        };
        int tileSize = WinoTokens.Px(context, Resource.Dimension.wino_winoaccount_mailbox_icon) + WinoTokens.Px(context, Resource.Dimension.wino_space_l);
        tile.AddView(new BrandIconView(context, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l),
            new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        tile.LayoutParameters = new LinearLayout.LayoutParams(tileSize, tileSize);
        var header = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_l, text, tile, text, Row(active, limitReached));
        header.SetGravity(GravityFlags.CenterVertical);

        var exhausted = Bar(WinoInfoBarSeverity.Informational, nameof(vm.ExhaustedBucketsText), s => s.HasExhaustedBuckets ? s.ExhaustedBucketsText : null);

        var planTitle = Text(Translator.WinoAccount_Management_SubscriptionLabel, WinoTextStyle.BodyStrong);
        var manage = Bind.Views.Command(IntelligenceViews.LinkButton(context, Translator.WinoIntelligence_ManageSubscription), vm.ManageSubscriptionCommand);
        Bind.Visible(manage, vm, nameof(vm.CanManageSubscription), s => s.CanManageSubscription);
        var refresh = Bind.Button(Translator.Buttons_Refresh, vm.RefreshPurchasesCommand);
        var plan = Stack(Resource.Dimension.wino_space_xs, planTitle, IntelligenceViews.Flow(context, [manage, refresh], Resource.Dimension.wino_space_s));

        var usage = Stack(0);
        void RebuildUsage()
        {
            usage.RemoveAllViews();
            foreach (var item in vm.IntelligenceUsageItems.ToList())
            {
                var name = Text(item.DisplayName, WinoTextStyle.Body, maxLines: 1);
                var value = Text(item.Value, WinoTextStyle.Description);
                var top = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_s, name, name, value);
                var meter = IntelligenceViews.Meter(context);
                meter.ContentDescription = item.DisplayName;
                IntelligenceViews.SetMeter(meter, item.Percentage, item.IsExhausted);
                AddTo(usage, Stack(Resource.Dimension.wino_space_xs, top, meter), Resource.Dimension.wino_space_l);
            }
        }
        Bind.Collection(vm.IntelligenceUsageItems, RebuildUsage);
        RebuildUsage();
        var unavailable = Text(Translator.WinoAccount_Management_IntelligenceUsageUnavailable, WinoTextStyle.Description);
        Bind.Visible(unavailable, vm, nameof(vm.IsIntelligenceUsageAvailable), s => !s.IsIntelligenceUsageAvailable);

        var column = Stack(Resource.Dimension.wino_space_l, header, exhausted);
        AddSeparator(column, Resource.Dimension.wino_space_l);
        AddTo(column, plan, Resource.Dimension.wino_space_l);
        AddSeparator(column, Resource.Dimension.wino_space_l);
        AddTo(column, usage, Resource.Dimension.wino_space_xs);
        AddTo(column, unavailable, Resource.Dimension.wino_space_m);

        var wash = WinoTokens.Color(context, Resource.Color.wino_winoaccount_brand_wash);
        var surface = new FrameLayout(context) { Background = IntelligenceViews.Shape(context, wash, WinoColors.CardStroke(context), Resource.Dimension.wino_radius_small) };
        WinoTokens.Pad(surface, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_l);
        surface.AddView(column, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return surface;
    }

    /// <summary>Languages: the default translation and summary languages.</summary>
    private View LanguagesExpander()
    {
        var vm = VM;
        var translate = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedDefaultTranslationLanguage), s => s.SelectedDefaultTranslationLanguage, (s, v) => s.SelectedDefaultTranslationLanguage = v);
        var summarize = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedSummarizeLanguage), s => s.SelectedSummarizeLanguage, (s, v) => s.SelectedSummarizeLanguage = v);
        return Expander(Translator.WinoIntelligence_Languages_Title, Translator.WinoIntelligence_Languages_Description, WinoIconGlyph.Translate, null,
            StackedCard(Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Title, Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Description, WinoIconGlyph.None, Row(translate)),
            StackedCard(Translator.SettingsAppPreferences_AiSummarizeLanguage_Title, Translator.SettingsAppPreferences_AiSummarizeLanguage_Description, WinoIconGlyph.None, Row(summarize)));
    }

    /// <summary>
    /// Privacy: the consent notices, the account-wide AI provider access switch, and Review privacy policy; what the
    /// switch allows and when it was accepted are the section footer.
    /// </summary>
    private View PrivacyGroup()
    {
        var vm = VM;
        var group = new WinoSettingsGroup(Ctx, Translator.WinoIntelligence_Privacy_Header);
        group.Add(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentErrorMessage), s => s.HasConsentError ? s.ConsentErrorMessage : null));
        group.Add(Bar(WinoInfoBarSeverity.Warning, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionPending ? Translator.WinoAccount_IntelligenceConsentDeletionPending : null));
        group.Add(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionFailed ? Translator.WinoAccount_IntelligenceConsentDeletionFailed : null));

        var consent = new WinoTextSwitch(Ctx, Translator.WinoAccount_Allowed, Translator.WinoAccount_NotAllowed, Translator.WinoIntelligence_Privacy_Title);
        _consent = consent;
        Bind.Bind(vm, nameof(vm.IsConsentBusy), s => s.IsConsentBusy, on => consent.IsEnabled = !on);
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, granted =>
        {
            if (!_applyingConsent) consent.IsOn = granted;
        });
        EventHandler toggled = async (_, _) => await ConsentToggledAsync();
        consent.Toggled += toggled;
        Bindings.Own(new ActionDisposable(() => consent.Toggled -= toggled));
        group.Add(Card(Translator.WinoIntelligence_Privacy_Title, Translator.WinoIntelligence_ProviderAccess_Description, WinoIconGlyph.Shield, consent));
        group.Add(ActionCard(Translator.WinoIntelligence_Policy_ReviewLink, vm.OpenConsentPolicyCommand));
        Bind.Bind(vm, nameof(vm.ConsentAcceptedText), s => s.ConsentAcceptedText, text => group.Footer = string.IsNullOrWhiteSpace(text)
            ? Translator.WinoIntelligence_ProviderAccess_Description
            : $"{Translator.WinoIntelligence_ProviderAccess_Description} {text}");
        return group;
    }

    private async Task ConsentToggledAsync()
    {
        var vm = VM;
        if (_consent is not { } consent || _applyingConsent || vm.IsConsentBusy || consent.IsOn == vm.IsConsentGranted) return;
        _applyingConsent = true;
        try
        {
            if (consent.IsOn)
            {
                await IntelligenceConsentPolicySheet.PresentAsync(Ctx, vm.ConsentPolicyUri, () => vm.SetIntelligenceConsentAsync(true));
            }
            else if (_dialogs is { } dialogs && await dialogs.ShowConfirmationDialogAsync(Translator.WinoAccount_IntelligenceConsentDisableMessage,
                         Translator.WinoAccount_IntelligenceConsentDisableTitle, Translator.WinoAccount_DisableConsent))
            {
                await vm.SetIntelligenceConsentAsync(false);
            }
        }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            _applyingConsent = false;
            consent.IsOn = vm.IsConsentGranted;
        }
    }

    #endregion
}
