using System.Windows.Input;
using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;
using Wino.Presentation.Bindings;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Wino Intelligence home (Windows WinoIntelligencePage, shared WinoAccountManagementPageViewModel).
/// Without access: the hero, "What's included" led by Wino Assistant, and the locked features.
/// With access: the status expander (subscription and this month's usage), then Wino Assistant,
/// Smart Labels and Daily Briefing, the languages expander and the AI provider access switch.
/// </summary>
public sealed class WinoIntelligencePageViewController(WinoAccountManagementPageViewModel viewModel,
    IMailDialogService dialogs, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<WinoAccountManagementPageViewModel>(viewModel, dispatcher, logger)
{
    private readonly NSStackView _content = WinoLayout.VStack(12);
    private readonly NSStackView _perkRows = WinoLayout.VStack(0);
    private readonly NSStackView _usageRows = WinoLayout.VStack(0);
    private NSView? _loading;
    private NSProgressIndicator? _loadingSpinner;
    private bool _revealed;
    private WinoTextSwitch? _consent;
    private bool _applyingConsent;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Page.Spacing = 12;

        // First load: the page starts with default ViewModel state (signed out, no access). Showing it
        // would flash the offer before the cached account state lands, so a spinner covers it until then.
        _loading = Add(IntelligenceViews.LoadingState(out var loadingSpinner));
        _loadingSpinner = loadingSpinner;
        _content.Alignment = NSLayoutAttribute.Leading;
        _content.Hidden = true;
        Add(_content);

        var spinner = IntelligenceViews.Spinner();
        var updating = Row(spinner, WinoStyle.Label(Translator.WinoIntelligence_Updating, WinoStyle.Body, WinoStyle.PrimaryText));
        var showUpdating = IntelligenceViews.DelayedProgressRow(updating, spinner, () => !Bindings.IsDisposed);
        Bind.Bind(vm, nameof(vm.IsIntelligenceRefreshing), s => s.IsIntelligenceRefreshing, on =>
        {
            if (on) Reveal();
            showUpdating(on);
        });
        AddTo(_content, updating);
        AddTo(_content, CommandBar(WinoInfoBarSeverity.Informational, nameof(vm.PurchaseStatusMessage), s => s.PurchaseStatusMessage,
            vm.RefreshPurchasesText, vm.RefreshPurchasesCommand));
        var refreshError = CommandBar(WinoInfoBarSeverity.Warning, nameof(vm.IntelligenceRefreshError), s => s.IntelligenceRefreshError,
            Translator.Buttons_Retry, vm.RetryIntelligenceRefreshCommand);
        refreshError.IsClosable = true;
        AddTo(_content, refreshError);
        AddTo(_content, Bar(WinoInfoBarSeverity.Informational, nameof(vm.SubscriptionEndedText), s => s.SubscriptionEndedText));
        AddTo(_content, Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentErrorMessage), s => s.HasConsentError ? s.ConsentErrorMessage : null));
        AddTo(_content, Bar(WinoInfoBarSeverity.Warning, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionPending ? Translator.WinoAccount_IntelligenceConsentDeletionPending : null));
        AddTo(_content, Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionFailed ? Translator.WinoAccount_IntelligenceConsentDeletionFailed : null));

        AddTo(_content, Bind.Visible(OfferPanel(), vm, nameof(vm.HasIntelligenceAccess), s => s.HasNoIntelligenceAccess));
        AddTo(_content, Bind.Visible(SubscribedPanel(), vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess));

        Bind.Collection(vm.OfferPerks, RebuildPerks);
        Bind.Collection(vm.IntelligenceUsageItems, RebuildUsage);
        RebuildPerks();
        RebuildUsage();
#if DEBUG
        WinoIntelligenceDebug.Current = vm;
#endif
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        try { await ViewModel.InitializeAsync(mode, parameter!); }
        finally { await Dispatcher.ExecuteOnUIThread(Reveal); }
    }

    private void Reveal()
    {
        if (_revealed || Bindings.IsDisposed) return;
        _revealed = true;
        _loadingSpinner?.StopAnimation(null);
        if (_loading is not null) _loading.Hidden = true;
        _content.Hidden = false;
    }

    private static T AddTo<T>(NSStackView stack, T view) where T : NSView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        view.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        if (view is NSStackView inner) inner.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        stack.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return view;
    }

    private static NSStackView Column(double spacing = 4)
    {
        var stack = WinoLayout.VStack(spacing);
        stack.Alignment = NSLayoutAttribute.Leading;
        return stack;
    }

    private WinoInfoBar Bar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message)
    {
        var bar = InfoBar(severity, null, null);
        Bind.Bind(ViewModel, property, message, text => { bar.Message = text; bar.Hidden = string.IsNullOrWhiteSpace(text); });
        return bar;
    }

    private WinoInfoBar CommandBar(WinoInfoBarSeverity severity, string property, Func<WinoAccountManagementPageViewModel, string?> message, string action, ICommand command)
    {
        var bar = Bar(severity, property, message);
        bar.ActionTitle = action;
        bar.ActionInvoked += (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
        return bar;
    }

    private static NSTextField Secondary(string? text, double size = 12)
        => WinoStyle.Label(text, NSFont.SystemFontOfSize((nfloat)size), WinoStyle.SecondaryText, 0);

    private static NSTextField SectionHeader(string text)
        => WinoStyle.Label(text, WinoSettingsStyle.SectionTitle, WinoStyle.PrimaryText);

    #region Offer

    private NSView OfferPanel()
    {
        var panel = Column(4);
        AddTo(panel, Hero());
        panel.SetCustomSpacing(12, panel.ArrangedSubviews[^1]);
        AddTo(panel, IncludedExpander());
        var locked = SectionHeader(Translator.WinoIntelligence_Locked_Caption);
        AddTo(panel, locked);
        panel.SetCustomSpacing(20, panel.ArrangedSubviews[^2]);
        AddTo(panel, IntelligenceSettingsViews.LockedCard(Translator.WinoIntelligence_MailClassification_Title, Translator.WinoIntelligence_MailClassification_Description, WinoIconGlyph.Mail));
        AddTo(panel, IntelligenceSettingsViews.LockedCard(Translator.WinoIntelligence_Languages_Title, Translator.WinoIntelligence_Languages_Description, WinoIconGlyph.Translate));
        return panel;
    }

    /// <summary>Illustration, headline and the one primary action: Get Wino Intelligence signed out, Subscribe signed in.</summary>
    private NSView Hero()
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustrationView();
        var title = WinoStyle.Label(Translator.WinoIntelligence_Hero_Title, NSFont.SystemFontOfSize(20, NSFontWeight.Semibold), WinoStyle.PrimaryText, 0);
        var description = Secondary(Translator.WinoIntelligence_Hero_Description, 13);
        description.WidthAnchor.ConstraintLessThanOrEqualTo(640).Active = true;

        var signedOut = Row(
            Bind.Button(Translator.WinoIntelligence_GetButton, vm.GetWinoIntelligenceCommand, primary: true),
            Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand));
        Bind.Visible(signedOut, vm, nameof(vm.IsSignedIn), s => s.IsSignedOut);

        var subscribe = Bind.Button(vm.HeroSubscribeButtonText, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        Bind.Bind(vm, nameof(vm.HeroSubscribeButtonText), s => s.HeroSubscribeButtonText, text => subscribe.Title = text);
        var signedInViews = new List<NSView> { subscribe, IntelligenceSettingsViews.UrlLink(Translator.Buttons_LearnMore, IntelligenceSettingsViews.LearnMoreUrl) };
        if (vm.UsesAppStorePurchases)
            signedInViews.Add(Bind.Label(vm, nameof(vm.AppStoreAiPackPriceText), s => s.AppStoreAiPackPriceText, NSFont.SystemFontOfSize(11), WinoStyle.SecondaryText, 0));
        var signedIn = Row(signedInViews.ToArray());
        Bind.Visible(signedIn, vm, nameof(vm.IsSignedIn), s => s.IsSignedIn);

        var error = WinoStyle.Label(null, WinoStyle.Caption, WinoStyle.Caution, 0);
        Bind.Bind(vm.AiPackAddOn, nameof(WinoAddOnItemViewModel.ErrorText), a => a.ShowErrorState ? a.ErrorText : null, text =>
        {
            error.StringValue = text ?? string.Empty;
            error.Hidden = string.IsNullOrWhiteSpace(text);
        });
        var terms = Bind.Label(vm, nameof(vm.IntelligenceHeroTermsText), s => s.IntelligenceHeroTermsText, WinoStyle.Caption, WinoStyle.SecondaryText, 0);

        var column = Column(4);
        foreach (var view in new NSView[] { title, description, signedOut, signedIn, error, terms }) column.AddArrangedSubview(view);
        column.SetCustomSpacing(16, description);
        column.SetCustomSpacing(16, signedOut);
        column.SetCustomSpacing(16, signedIn);
        foreach (var label in new NSView[] { description, error, terms }) label.WidthAnchor.ConstraintLessThanOrEqualTo(column.WidthAnchor).Active = true;
        if (vm.UsesAppStorePurchases)
        {
            // Guideline 3.1.2: the length, renewal and the Terms of Use and Privacy Policy links sit next to the purchase.
            var appStoreTerms = AppStoreSubscriptionTerms.Create();
            column.AddArrangedSubview(appStoreTerms);
            appStoreTerms.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        }
        column.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);

        var row = WinoLayout.HStack(20, art, column);
        row.Alignment = NSLayoutAttribute.CenterY;
        return IntelligenceViews.Surface(row, 20, 8);
    }

    /// <summary>"What's included": Wino Assistant first (logo, New badge, allowance), then one card per perk.</summary>
    private NSView IncludedExpander()
    {
        var expander = new WinoSettingsExpander(Translator.WinoIntelligence_Included_Title, Translator.WinoIntelligence_Included_Description, WinoIconGlyph.None, null, isExpanded: true);
        expander.HeaderCard.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);

        var assistant = Card(Translator.WinoIntelligence_Perk_Voice_Title,
            $"{Translator.WinoIntelligence_Perk_Voice_Description}\n{Translator.WinoIntelligence_Perk_Voice_Allowance}");
        assistant.LeadingView = IntelligenceSettingsViews.AppLogo(36);
        assistant.Content = IntelligenceSettingsViews.AccentPill(Translator.WinoIntelligence_NewBadge);
        expander.Add(assistant);

        _perkRows.Alignment = NSLayoutAttribute.Leading;
        expander.Add(_perkRows, 0, 0, 0, 0);
        return expander;
    }

    private void RebuildPerks()
    {
        IntelligenceSettingsViews.Clear(_perkRows);
        foreach (var perk in ViewModel.OfferPerks.ToList())
        {
            var card = Card(perk.Title, perk.Description, perk.Icon, string.IsNullOrWhiteSpace(perk.TrailingText) ? null : Caption(perk.TrailingText));
            card.IsNested = true;
            _perkRows.AddArrangedSubview(card);
            card.WidthAnchor.ConstraintEqualTo(_perkRows.WidthAnchor).Active = true;
        }
    }

    #endregion

    #region Subscribed

    private NSView SubscribedPanel()
    {
        var vm = ViewModel;
        var panel = Column(4);
        AddTo(panel, StatusExpander());
        panel.SetCustomSpacing(12, panel.ArrangedSubviews[^1]);

        var assistant = CommandCard(Translator.WinoIntelligence_Voice_Title, vm.AssistantHomeDescription, WinoIconGlyph.None, vm.OpenVoiceControlCommand);
        assistant.LeadingView = IntelligenceSettingsViews.AppLogo(32);
        Bind.Bind(vm, nameof(vm.AssistantHomeDescription), s => s.AssistantHomeDescription, text => assistant.Description = text);
        AddTo(panel, Bind.Visible(assistant, vm, nameof(vm.HasIntelligenceAccess), s => s.ShowVoiceCommandsCard));

        var classification = CommandCard(Translator.WinoIntelligence_MailClassification_Title, Translator.WinoIntelligence_MailClassification_HomeDescription, WinoIconGlyph.Tag, vm.OpenMailClassificationCommand);
        AddTo(panel, classification);

        AddTo(panel, LanguagesExpander());

        var privacyHeader = SectionHeader(Translator.WinoIntelligence_Privacy_Header);
        AddTo(panel, privacyHeader);
        panel.SetCustomSpacing(20, panel.ArrangedSubviews[^2]);
        AddTo(panel, ProviderAccessCard());
        return panel;
    }

    /// <summary>AI provider access: the account-wide consent switch, the accepted date and the policy link.</summary>
    private NSView ProviderAccessCard()
    {
        var vm = ViewModel;
        var busy = IntelligenceViews.Spinner();
        _consent = new WinoTextSwitch(Translator.WinoAccount_Allowed, Translator.WinoAccount_NotAllowed, Translator.WinoIntelligence_Privacy_Title);
        Bind.Bind(vm, nameof(vm.IsConsentBusy), s => s.IsConsentBusy, on => { IntelligenceViews.SetSpinning(busy, on); _consent.IsEnabled = !on; });
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, granted => { if (!_applyingConsent) _consent.IsOn = granted; });
        _consent.Toggled += async (_, _) => await ConsentToggledAsync();

        var policy = IntelligenceSettingsViews.Link(Bind.Button(Translator.WinoIntelligence_Policy_ReviewLink, vm.OpenConsentPolicyCommand),
            Translator.WinoIntelligence_Policy_ReviewLink);
        var card = Card(Translator.WinoIntelligence_Privacy_Title, Translator.WinoIntelligence_ProviderAccess_Description, WinoIconGlyph.Shield, Row(policy, busy, _consent));
        Bind.Bind(vm, nameof(vm.ConsentAcceptedText), s => s.ConsentAcceptedText, text => card.Description = string.IsNullOrWhiteSpace(text)
            ? Translator.WinoIntelligence_ProviderAccess_Description
            : $"{Translator.WinoIntelligence_ProviderAccess_Description}\n{text}");
        return card;
    }

    private async Task ConsentToggledAsync()
    {
        var vm = ViewModel;
        if (_consent is null || _applyingConsent || vm.IsConsentBusy || _consent.IsOn == vm.IsConsentGranted) return;
        _applyingConsent = true;
        try
        {
            if (_consent.IsOn)
            {
                if (View.Window is { } window)
                    await IntelligenceConsentPolicySheet.PresentAsync(window, vm.ConsentPolicyUri, () => vm.SetIntelligenceConsentAsync(true));
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
            _consent.IsOn = vm.IsConsentGranted;
        }
    }

    /// <summary>The status strip: brand tile, title, renewal line and an Active or Limit reached badge; inside, the plan and usage.</summary>
    private NSView StatusExpander()
    {
        var vm = ViewModel;
        var active = Bind.Visible(IntelligenceSettingsViews.SuccessPill(Translator.WinoAccount_Management_ActiveShort), vm, nameof(vm.IsLimitReached), s => s.IsWithinLimits);
        var limit = Bind.Visible(IntelligenceSettingsViews.CriticalPill(Translator.WinoIntelligence_Status_LimitReached), vm, nameof(vm.IsLimitReached), s => s.IsLimitReached);
        var expander = new WinoSettingsExpander(Translator.WinoIntelligence_SettingsTitle, null, WinoIconGlyph.None, Row(active, limit));
        var tile = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(tile, 44, 44);
        var brand = new BrandIconView(WinoIconGlyph.WinoIntelligence, 22);
        tile.AddSubview(brand);
        brand.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor).Active = true;
        brand.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor).Active = true;
        expander.HeaderCard.LeadingView = tile;
        Bind.Bind(vm, nameof(vm.SubscriptionRenewalLine), s => s.SubscriptionRenewalLine, text => expander.HeaderCard.Description = string.IsNullOrWhiteSpace(text) ? null : text);

        // One body stack: a hidden arranged view collapses, so the allowance bar leaves no gap when it is not shown.
        var body = WinoLayout.VStack(0);
        body.Alignment = NSLayoutAttribute.Leading;
        var exhausted = Bar(WinoInfoBarSeverity.Warning, nameof(vm.ExhaustedBucketsText), s => s.HasExhaustedBuckets ? s.ExhaustedBucketsText : null);
        var exhaustedHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(exhausted, exhaustedHost, 8, 16, 8, 16);
        Bind.Visible(exhaustedHost, vm, nameof(vm.ExhaustedBucketsText), s => s.HasExhaustedBuckets);

        var manage = Bind.Button(Translator.WinoIntelligence_ManageSubscription, vm.ManageSubscriptionCommand);
        Bind.Visible(manage, vm, nameof(vm.CanManageSubscription), s => s.CanManageSubscription);
        var refresh = Bind.Button(Translator.Buttons_Refresh, vm.RefreshPurchasesCommand);
        var plan = Card(Translator.WinoAccount_Management_SubscriptionLabel, null, WinoIconGlyph.Payment, Row(manage, refresh));
        plan.IsNested = true;
        Bind.Bind(vm, nameof(vm.AiPackRenewalOrCancellationText), s => s.AiPackRenewalOrCancellationText, text => plan.Description = string.IsNullOrWhiteSpace(text) ? null : text);

        _usageRows.Alignment = NSLayoutAttribute.Leading;
        var unavailable = Card(Translator.WinoAccount_Management_IntelligenceUsageUnavailable, null);
        unavailable.IsNested = true;
        Bind.Visible(unavailable, vm, nameof(vm.IsIntelligenceUsageAvailable), s => !s.IsIntelligenceUsageAvailable);
        foreach (var view in new NSView[] { exhaustedHost, plan, _usageRows, unavailable })
        {
            body.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(body.WidthAnchor).Active = true;
        }
        expander.Add(body, 0, 0, 0, 0);
#if DEBUG
        WinoIntelligenceDebug.OpenQuota = () => expander.IsExpanded = true;
#endif
        return expander;
    }

    private void RebuildUsage()
    {
        IntelligenceSettingsViews.Clear(_usageRows);
        foreach (var item in ViewModel.IntelligenceUsageItems.ToList())
        {
            var value = WinoStyle.Label(item.Value, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
            value.Alignment = NSTextAlignment.Right;
            var bar = new WinoBarView(200) { Value = item.Percentage, ShowError = item.IsExhausted };
            var stack = WinoLayout.VStack(6, value, bar);
            stack.Alignment = NSLayoutAttribute.Trailing;
            WinoLayout.Size(stack, 200);
            var card = Card(item.DisplayName, null, WinoIconGlyph.None, stack);
            card.IsNested = true;
            _usageRows.AddArrangedSubview(card);
            card.WidthAnchor.ConstraintEqualTo(_usageRows.WidthAnchor).Active = true;
        }
    }

    private NSView LanguagesExpander()
    {
        var vm = ViewModel;
        var translate = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedDefaultTranslationLanguage), s => s.SelectedDefaultTranslationLanguage, (s, v) => s.SelectedDefaultTranslationLanguage = v);
        var summarize = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedSummarizeLanguage), s => s.SelectedSummarizeLanguage, (s, v) => s.SelectedSummarizeLanguage = v);
        return Expander(Translator.WinoIntelligence_Languages_Title, Translator.WinoIntelligence_Languages_Description, WinoIconGlyph.Translate, null,
            Card(Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Title, Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Description, WinoIconGlyph.None, translate),
            Card(Translator.SettingsAppPreferences_AiSummarizeLanguage_Title, Translator.SettingsAppPreferences_AiSummarizeLanguage_Description, WinoIconGlyph.None, summarize));
    }

    #endregion
}
