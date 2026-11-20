using System.Windows.Input;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;
using Wino.Mail.ViewModels.Common;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Wino Intelligence home (Windows WinoIntelligencePage, shared WinoAccountManagementPageViewModel), in the Windows
/// section order. Without access: the hero with the one call to action, "What's included" and the locked features.
/// With access: the status group (subscription and this month's usage), Wino Assistant where the platform has it, Smart
/// Labels and Daily Briefing, the languages and the Privacy section with the AI provider access switch.
/// </summary>
public sealed class WinoIntelligencePageViewController(WinoAccountManagementPageViewModel viewModel,
    IMailDialogService dialogs, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<WinoAccountManagementPageViewModel>(viewModel, dispatcher, logger)
{
    internal const string LearnMoreUrl = "https://www.winomail.app/intelligence";
    private readonly UIStackView _usageRows = WinoLayout.VStack(14);
    private readonly WinoSettingsGroup _perks = new(Translator.WinoIntelligence_Included_Title, Translator.WinoIntelligence_Included_Description);
    private readonly UIStackView _content = WinoLayout.VStack(WinoSettingsStyle.GroupSpacing);
    private WinoTextSwitch? _consent;
    private UIView? _loading;
    private UIActivityIndicatorView? _loadingSpinner;
    private bool _applyingConsent;
    private bool _revealed;

    protected override void BuildPage()
    {
        var vm = ViewModel;

        // First load: the page starts with default ViewModel state (signed out, no access). Showing it would flash the
        // offer before the cached account state lands, so the content stays hidden behind one spinner until then.
        _loading = Add(IntelligenceViews.LoadingState(out var loadingSpinner));
        _loadingSpinner = loadingSpinner;
        _content.Alignment = UIStackViewAlignment.Fill;
        _content.Hidden = true;
        Add(_content);

        // The network refresh row appears only when the refresh is slow, so a quick refresh does not push the rows down.
        var spinner = IntelligenceViews.Spinner();
        var updating = Row(spinner, WinoStyle.Label(Translator.WinoIntelligence_Updating, WinoStyle.Body, WinoStyle.PrimaryText));
        var showUpdating = IntelligenceViews.DelayedProgressRow(updating, spinner, () => !Bindings.IsDisposed);
        Bind.Bind(vm, nameof(vm.IsIntelligenceRefreshing), s => s.IsIntelligenceRefreshing, on =>
        {
            // The refresh starts after the account state and the cached snapshot are applied.
            if (on) Reveal();
            showUpdating(on);
        });
        AddContent(updating);
        AddContent(CommandBar(WinoInfoBarSeverity.Informational, nameof(vm.PurchaseStatusMessage), s => s.PurchaseStatusMessage,
            vm.RefreshPurchasesText, vm.RefreshPurchasesCommand));
        var refreshError = CommandBar(WinoInfoBarSeverity.Warning, nameof(vm.IntelligenceRefreshError), s => s.IntelligenceRefreshError,
            Translator.Buttons_Retry, vm.RetryIntelligenceRefreshCommand);
        refreshError.IsClosable = true;
        AddContent(refreshError);
        AddContent(Bar(WinoInfoBarSeverity.Warning, nameof(vm.SubscriptionEndedText), s => s.IsAiPackEnded ? s.SubscriptionEndedText : null, nameof(vm.IsAiPackEnded)));
        AddContent(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentErrorMessage), s => s.HasConsentError ? s.ConsentErrorMessage : null));
        AddContent(Bar(WinoInfoBarSeverity.Warning, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionPending ? Translator.WinoAccount_IntelligenceConsentDeletionPending : null));
        AddContent(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionFailed ? Translator.WinoAccount_IntelligenceConsentDeletionFailed : null));

        AddContent(Bind.Visible(OfferPanel(), vm, nameof(vm.HasIntelligenceAccess), s => s.HasNoIntelligenceAccess));
        AddContent(Bind.Visible(SubscribedPanel(), vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess));

        // Windows keeps Refresh in the subscription card; on iOS a page's Refresh is a bar button (18 Oct 2026).
        var refresh = BarButton(WinoIconGlyph.ArrowClockwise, Translator.Buttons_Refresh, vm.RefreshPurchasesCommand);
        Bind.Bind(vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess, on => SetBarItemVisible(refresh, on));

        // A snapshot apply clears and refills the collections.
        Bind.Collection(vm.OfferPerks, RebuildPerks);
        Bind.Collection(vm.IntelligenceUsageItems, RebuildUsage);
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        try { await ViewModel.InitializeAsync(mode, parameter); }
        finally { await Dispatcher.ExecuteOnUIThread(Reveal); }
    }

    private void Reveal()
    {
        if (_revealed || Bindings.IsDisposed) return;
        _revealed = true;
        _loadingSpinner?.StopAnimating();
        if (_loading is not null) _loading.Hidden = true;
        _content.Hidden = false;
    }

    private T AddContent<T>(T view) where T : UIView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _content.AddArrangedSubview(view);
        return view;
    }

    private static UIStackView Panel(params UIView[] views)
    {
        var panel = WinoLayout.VStack(WinoSettingsStyle.GroupSpacing, views);
        panel.Alignment = UIStackViewAlignment.Fill;
        return panel;
    }

    /// <summary>An info bar whose message is re-read when <paramref name="property"/> or any of <paramref name="alsoWatch"/> changes.</summary>
    private WinoInfoBar Bar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message, params string[] alsoWatch)
    {
        var bar = InfoBar(severity, null, null);
        foreach (var watched in alsoWatch.Prepend(property))
            Bind.Bind(ViewModel, watched, message, text => { bar.Message = text; bar.Hidden = string.IsNullOrWhiteSpace(text); });
        return bar;
    }

    private WinoInfoBar CommandBar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message, string action, ICommand command)
    {
        var bar = Bar(severity, property, message);
        bar.ActionTitle = action;
        EventHandler handler = (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
        bar.ActionInvoked += handler;
        Bindings.Own(new ActionDisposable(() => bar.ActionInvoked -= handler));
        return bar;
    }

    private static UILabel Secondary(string text, UIFont? font = null)
        => WinoStyle.Label(text, font ?? WinoStyle.Description, WinoStyle.SecondaryText, 0);

    private static UIView Inset(UIView view)
    {
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(view, host, 0, WinoSettingsStyle.CardPadding, 0, WinoSettingsStyle.CardPadding);
        return host;
    }

    private static void SetTitle(UIButton button, string? text)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = text ?? string.Empty;
        button.Configuration = configuration;
    }

    private static UIView Pill(string text, UIColor textColor, UIColor fill)
    {
        var label = WinoStyle.Label(text, WinoStyle.Caption, textColor);
        WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        var pill = new WinoSurfaceView { Fill = fill, CornerRadius = 10, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, pill, 2, 10, 2, 10);
        WinoLayout.Priorities(pill, UILayoutConstraintAxis.Horizontal, 751, 1000);
        pill.IsAccessibilityElement = true;
        pill.AccessibilityTraits = UIAccessibilityTrait.StaticText;
        pill.AccessibilityLabel = text;
        return pill;
    }

    /// <summary>The app logo (Windows StoreLogo) for the Wino Assistant rows, or the brand glyph when it is not bundled.</summary>
    private static UIView AppLogo(double size)
    {
        if (UIImage.FromBundle("WinoLogo") is not { } logo) return new BrandIconView(WinoIconGlyph.WinoIntelligence, size * 0.6);
        var image = new UIImageView(logo) { ContentMode = UIViewContentMode.ScaleAspectFit, IsAccessibilityElement = false, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(image, size, size);
        return image;
    }

    #region Offer

    private UIView OfferPanel()
        => Panel(Hero(), _perks, Group(Translator.WinoIntelligence_Locked_Caption,
            LockedRow(Translator.WinoIntelligence_MailClassification_Title, Translator.WinoIntelligence_MailClassification_Description),
            LockedRow(Translator.WinoIntelligence_Languages_Title, Translator.WinoIntelligence_Languages_Description)));

    /// <summary>Illustration, headline and the one primary action: Get Wino Intelligence signed out, Subscribe signed in.</summary>
    private UIView Hero()
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustrationView(72);
        // Wino Assistant needs Voice Control; without it the hero leaves the assistant out of its promise.
        var title = WinoStyle.Label(vm.IsVoiceControlAvailable ? Translator.WinoIntelligence_Hero_Title : Translator.WinoIntelligence_SettingsTitle,
            WinoStyle.Heading, WinoStyle.PrimaryText, 0);
        title.AccessibilityTraits = UIAccessibilityTrait.Header;
        var description = Secondary(vm.IsVoiceControlAvailable ? Translator.WinoIntelligence_Hero_Description : Translator.WinoIntelligence_Hero_Description_NoAssistant);
        var text = WinoLayout.VStack(4, title, description);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        var top = WinoLayout.HStack(16, art, text);
        top.Alignment = UIStackViewAlignment.Center;

        var get = Bind.Button(Translator.WinoIntelligence_GetButton, vm.GetWinoIntelligenceCommand, primary: true);
        get.AccessibilityIdentifier = "WinoIntelligenceSignInButton";
        var create = Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand);
        create.AccessibilityIdentifier = "WinoIntelligenceCreateAccountButton";
        var signedOut = WinoLayout.VStack(8, get, create);
        signedOut.Alignment = UIStackViewAlignment.Fill;
        Bind.Visible(signedOut, vm, nameof(vm.IsSignedIn), s => s.IsSignedOut);

        var subscribe = Bind.Button(vm.HeroSubscribeButtonText, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        subscribe.AccessibilityIdentifier = "WinoIntelligenceSubscribeButton";
        Bind.Bind(vm, nameof(vm.HeroSubscribeButtonText), s => s.HeroSubscribeButtonText, value => SetTitle(subscribe, value));
        var learn = IntelligenceViews.LinkButton(Translator.Buttons_LearnMore);
        learn.AccessibilityIdentifier = "WinoIntelligenceLearnMoreButton";
        Bind.OnActivated(learn, () => IntelligenceViews.OpenUrl(LearnMoreUrl));
        var signedInViews = new List<UIView> { subscribe };
        if (vm.UsesAppStorePurchases)
            signedInViews.Add(Bind.Label(vm, nameof(vm.AppStoreAiPackPriceText), s => s.AppStoreAiPackPriceText, WinoStyle.Caption, WinoStyle.SecondaryText, 0));
        signedInViews.Add(WinoLayout.HStack(0, learn, WinoLayout.Spacer()));
        var signedIn = WinoLayout.VStack(6, signedInViews.ToArray());
        signedIn.Alignment = UIStackViewAlignment.Fill;
        Bind.Visible(signedIn, vm, nameof(vm.IsSignedIn), s => s.IsSignedIn);

        var addOn = vm.AiPackAddOn;
        var error = Bind.Label(addOn, nameof(addOn.ErrorText), a => a.ErrorText, WinoStyle.Caption, WinoStyle.Caution, 0);
        Bind.Visible(error, addOn, nameof(addOn.ShowErrorState), a => a.ShowErrorState);
        var terms = Bind.Label(vm, nameof(vm.IntelligenceHeroTermsText), s => s.IntelligenceHeroTermsText, WinoStyle.Caption, WinoStyle.SecondaryText, 0);

        var column = WinoLayout.VStack(8, top, signedOut, signedIn, error, terms);
        column.Alignment = UIStackViewAlignment.Fill;
        column.SetCustomSpacing(16, top);
        column.SetCustomSpacing(12, signedOut);
        column.SetCustomSpacing(12, signedIn);
        if (vm.UsesAppStorePurchases)
        {
            // Guideline 3.1.2: the length, renewal and the Terms of Use and Privacy Policy links sit next to the purchase.
            column.AddArrangedSubview(AppStoreSubscriptionTerms.Create());
        }

        var surface = new WinoSurfaceView
        {
            // Windows WinoIntelligenceBrandSurfaceBrush: a faint wash of the brand colour.
            Fill = WinoStyle.Dynamic(WinoStyle.Hex(0x3B84E8, 0.07), WinoStyle.Hex(0x7FBAFF, 0.08)),
            Stroke = WinoSettingsStyle.CardStroke,
            CornerRadius = WinoSettingsStyle.CardRadius,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        surface.AccessibilityIdentifier = "WinoIntelligenceHero";
        WinoLayout.Fill(column, surface, 16);
        return surface;
    }

    /// <summary>Wino Assistant first where the platform has Voice Control, then one row per perk with its allowance or usage.</summary>
    private void RebuildPerks()
    {
        _perks.Clear();
        _perks.AccessibilityIdentifier = "WinoIntelligenceIncludedExpander";
        if (ViewModel.IsVoiceControlAvailable)
        {
            var assistant = Card(Translator.WinoIntelligence_Perk_Voice_Title,
                $"{Translator.WinoIntelligence_Perk_Voice_Description}\n{Translator.WinoIntelligence_Perk_Voice_Allowance}",
                WinoIconGlyph.None, Pill(Translator.WinoIntelligence_NewBadge, UIColor.White, WinoStyle.Accent));
            assistant.LeadingView = AppLogo(36);
            _perks.Add(assistant);
        }
        foreach (var perk in ViewModel.OfferPerks.ToList())
        {
            // The allowance reads under the description; beside it, long descriptions squeeze it to an ellipsis on phone width.
            var allowance = WinoStyle.Label(perk.TrailingText, WinoSettingsStyle.CardDescription, WinoStyle.Accent);
            _perks.Add(StackedCard(perk.Title, perk.Description, WinoIconGlyph.None, allowance));
        }
    }

    private WinoSettingsCard LockedRow(string title, string description)
    {
        var card = Card(title, description, WinoIconGlyph.None, new WinoIconView(WinoIconGlyph.LockClosed, 14, WinoStyle.TertiaryText));
        card.IsEnabled = false;
        return card;
    }

    #endregion

    #region Subscribed

    private UIView SubscribedPanel() => Panel(StatusGroup(), FeaturesGroup(), LanguagesGroup(), PrivacyGroup());

    /// <summary>Status: Active or Limit reached with the renewal line, the plan, then this month's usage per bucket.</summary>
    private UIView StatusGroup()
    {
        var vm = ViewModel;
        var active = Bind.Visible(Pill(Translator.WinoAccount_Management_ActiveShort, WinoStyle.Success, WinoStyle.Dynamic(WinoStyle.Hex(0xDFF6DD), WinoStyle.Hex(0x393D1B))),
            vm, nameof(vm.IsLimitReached), s => s.IsWithinLimits);
        var limit = Bind.Visible(Pill(Translator.WinoIntelligence_Status_LimitReached, WinoStyle.Critical, WinoStyle.Dynamic(WinoStyle.Hex(0xFDE7E9), WinoStyle.Hex(0x442726))),
            vm, nameof(vm.IsLimitReached), s => s.IsLimitReached);
        var status = Card(Translator.WinoIntelligence_SettingsTitle, null, WinoIconGlyph.None, WinoLayout.HStack(8, active, limit));
        status.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 22);
        Bind.Bind(vm, nameof(vm.SubscriptionRenewalLine), s => s.SubscriptionRenewalLine, text => status.Description = string.IsNullOrWhiteSpace(text) ? null : text);

        // App Store subscriptions are managed in the App Store, so the plan row is left out there.
        var manage = Bind.Button(Translator.WinoIntelligence_ManageSubscription, vm.ManageSubscriptionCommand);
        manage.AccessibilityIdentifier = "WinoIntelligenceManageSubscriptionButton";
        var plan = Bind.Visible(Card(Translator.WinoAccount_Management_SubscriptionLabel, null, WinoIconGlyph.None, manage),
            vm, nameof(vm.CanManageSubscription), s => s.CanManageSubscription);

        var group = Group(null, status, plan);
        group.AccessibilityIdentifier = "WinoIntelligenceStatusExpander";
        group.Add(Bar(WinoInfoBarSeverity.Informational, nameof(vm.ExhaustedBucketsText), s => s.HasExhaustedBuckets ? s.ExhaustedBucketsText : null, nameof(vm.HasExhaustedBuckets)));
        _usageRows.Alignment = UIStackViewAlignment.Fill;
        var usage = IntelligenceViews.Surface(_usageRows, 16, WinoSettingsStyle.CardRadius);
        usage.AccessibilityIdentifier = "WinoIntelligenceUsageCard";
        Bind.Visible(usage, vm, nameof(vm.IsIntelligenceUsageAvailable), s => s.IsIntelligenceUsageAvailable);
        group.Add(usage);
        var unavailable = Inset(Secondary(Translator.WinoAccount_Management_IntelligenceUsageUnavailable));
        Bind.Visible(unavailable, vm, nameof(vm.IsIntelligenceUsageAvailable), s => !s.IsIntelligenceUsageAvailable);
        group.Add(unavailable);
        return group;
    }

    /// <summary>Windows IntelligenceUsageBucketTemplate: name and value, then the bar (red once the bucket is used up).</summary>
    private void RebuildUsage()
    {
        IntelligenceViews.Clear(_usageRows);
        foreach (var item in ViewModel.IntelligenceUsageItems.ToList())
        {
            var name = WinoStyle.Label(item.DisplayName, WinoStyle.Body, WinoStyle.PrimaryText);
            name.LineBreakMode = UILineBreakMode.TailTruncation;
            WinoLayout.Priorities(name, UILayoutConstraintAxis.Horizontal, 1, 250);
            var value = WinoStyle.Label(item.Value, WinoStyle.Description, WinoStyle.SecondaryText);
            WinoLayout.Priorities(value, UILayoutConstraintAxis.Horizontal, 750, 750);
            var top = WinoLayout.HStack(8, name, value);
            var bar = new WinoBarView { Value = item.Percentage, ShowError = item.IsExhausted };
            WinoAccessibility.Label(bar, item.DisplayName);
            bar.AccessibilityIdentifier = item.Bucket;
            var row = WinoLayout.VStack(6, top, bar);
            row.Alignment = UIStackViewAlignment.Fill;
            _usageRows.AddArrangedSubview(row);
        }
    }

    /// <summary>Wino Assistant (only where the platform has Voice Control), then Smart Labels and Daily Briefing.</summary>
    private UIView FeaturesGroup()
    {
        var vm = ViewModel;
        var assistant = CommandCard(Translator.WinoIntelligence_Voice_Title, vm.AssistantHomeDescription, WinoIconGlyph.None, vm.OpenVoiceControlCommand);
        assistant.AccessibilityIdentifier = "WinoIntelligenceVoiceCommandsCard";
        assistant.LeadingView = AppLogo(32);
        Bind.Bind(vm, nameof(vm.AssistantHomeDescription), s => s.AssistantHomeDescription, text => assistant.Description = text);
        Bind.Visible(assistant, vm, nameof(vm.HasIntelligenceAccess), s => s.ShowVoiceCommandsCard);

        var classification = CommandCard(Translator.WinoIntelligence_MailClassification_Title, Translator.WinoIntelligence_MailClassification_HomeDescription,
            WinoIconGlyph.None, vm.OpenMailClassificationCommand);
        classification.AccessibilityIdentifier = "WinoIntelligenceMailClassificationCard";
        return Group(null, assistant, classification);
    }

    /// <summary>Languages: the default translation and summary languages, each a value row that pushes its list.</summary>
    private UIView LanguagesGroup()
    {
        var vm = ViewModel;
        var translate = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedDefaultTranslationLanguage), s => s.SelectedDefaultTranslationLanguage, (s, v) => s.SelectedDefaultTranslationLanguage = v);
        var summarize = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedSummarizeLanguage), s => s.SelectedSummarizeLanguage, (s, v) => s.SelectedSummarizeLanguage = v);
        var group = Group(Translator.WinoIntelligence_Languages_Title,
            Card(Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Title, Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Description, WinoIconGlyph.None, translate),
            Card(Translator.SettingsAppPreferences_AiSummarizeLanguage_Title, Translator.SettingsAppPreferences_AiSummarizeLanguage_Description, WinoIconGlyph.None, summarize));
        group.Footer = Translator.WinoIntelligence_Languages_Description;
        group.AccessibilityIdentifier = "WinoIntelligenceLanguagesExpander";
        return group;
    }

    /// <summary>Privacy: the account-wide AI provider access switch with the accepted date, and the policy link.</summary>
    private UIView PrivacyGroup()
    {
        var vm = ViewModel;
        var busy = IntelligenceViews.Spinner();
        var consent = new WinoTextSwitch(Translator.WinoAccount_Allowed, Translator.WinoAccount_NotAllowed, Translator.WinoIntelligence_Privacy_Title);
        _consent = consent;
        var card = Card(Translator.WinoIntelligence_Privacy_Title, Translator.WinoIntelligence_ProviderAccess_Description, WinoIconGlyph.None, Row(busy, consent));
        card.AccessibilityIdentifier = "WinoIntelligenceProviderAccessCard";
        Bind.Bind(vm, nameof(vm.ConsentAcceptedText), s => s.ConsentAcceptedText, text => card.Description = string.IsNullOrWhiteSpace(text)
            ? Translator.WinoIntelligence_ProviderAccess_Description
            : $"{Translator.WinoIntelligence_ProviderAccess_Description}\n{text}");
        Bind.Bind(vm, nameof(vm.IsConsentBusy), s => s.IsConsentBusy, on => { IntelligenceViews.SetSpinning(busy, on); consent.IsEnabled = !on; });
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, granted => { if (!_applyingConsent) consent.IsOn = granted; });
        EventHandler toggled = async (_, _) => await ConsentToggledAsync();
        consent.Toggled += toggled;
        Bindings.Own(new ActionDisposable(() => consent.Toggled -= toggled));

        var policy = Bind.Button(Translator.WinoIntelligence_Policy_ReviewLink, vm.OpenConsentPolicyCommand);
        policy.AccessibilityIdentifier = "WinoIntelligencePolicyReviewButton";
        return Group(Translator.WinoIntelligence_Privacy_Header, card, ActionRow(policy));
    }

    private async Task ConsentToggledAsync()
    {
        var vm = ViewModel;
        if (_consent is not { } consent || _applyingConsent || vm.IsConsentBusy || consent.IsOn == vm.IsConsentGranted) return;
        _applyingConsent = true;
        try
        {
            if (consent.IsOn)
            {
                await IntelligenceConsentPolicySheet.PresentAsync(this, vm.ConsentPolicyUri, () => vm.SetIntelligenceConsentAsync(true));
            }
            else if (await dialogs.ShowConfirmationDialogAsync(Translator.WinoAccount_IntelligenceConsentDisableMessage,
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
