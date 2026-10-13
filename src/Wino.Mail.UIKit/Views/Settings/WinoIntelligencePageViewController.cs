using System.Windows.Input;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Wino Intelligence hub (Windows WinoIntelligencePage, shared WinoAccountManagementPageViewModel):
/// the signed-out and not-subscribed promotions, the subscription card with the quota flyout,
/// consent and mailbox rows, and the translation languages. The Mac side-by-side promotions stack
/// vertically on phone width.
/// </summary>
public sealed class WinoIntelligencePageViewController(WinoAccountManagementPageViewModel viewModel, IPictureStorageService pictures,
    IMailDialogService dialogs, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<WinoAccountManagementPageViewModel>(viewModel, dispatcher, logger)
{
    private const string LearnMoreUrl = "https://www.winomail.app/intelligence";
    private readonly UIStackView _mailboxRows = WinoLayout.VStack(0);
    private WinoTextSwitch? _consent;
    private readonly UIStackView _content = WinoLayout.VStack(12);
    private UIView? _loading;
    private UIActivityIndicatorView? _loadingSpinner;
    private BindingScope? _rowScope;
    private bool _applyingConsent;
    private bool _revealed;
    private bool _mailboxRebuildQueued;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        Page.Spacing = 12;

        // First load: the page starts with default ViewModel state (signed out, not subscribed, no
        // mailboxes). Showing it would flash the promotions before the cached account state lands, so
        // the content stays hidden behind one spinner until the cached state is applied.
        _loading = Add(IntelligenceViews.LoadingState(out var loadingSpinner));
        _loadingSpinner = loadingSpinner;
        _content.Alignment = UIStackViewAlignment.Fill;
        _content.Hidden = true;
        Add(_content);

        // Status rows shared by every state. The network refresh row appears only when the refresh
        // is slow, so a quick refresh does not push the cards down and back up.
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
            Translator.WinoAccount_Management_RefreshPurchases, vm.RefreshPurchasesCommand));
        // Windows: the refresh error bar is IsClosable="True"; the purchase status bar is not closable.
        var refreshError = CommandBar(WinoInfoBarSeverity.Warning, nameof(vm.IntelligenceRefreshError), s => s.IntelligenceRefreshError,
            Translator.Buttons_Retry, vm.RetryIntelligenceRefreshCommand);
        refreshError.IsClosable = true;
        AddContent(refreshError);

        AddContent(Bind.Visible(SignedOutPromo(), vm, nameof(vm.IsSignedIn), s => s.IsSignedOut));

        var signedIn = WinoLayout.VStack(12);
        signedIn.Alignment = UIStackViewAlignment.Fill;
        void AddSigned(UIView view)
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            signedIn.AddArrangedSubview(view);
        }
        var promo = NotSubscribedPromo();
        Bind.Bind(vm.AiPackAddOn, nameof(WinoAddOnItemViewModel.IsPurchased), a => a.IsNotPurchased, show => promo.Hidden = !show);
        AddSigned(promo);
        AddSigned(SubscriptionCard());
        AddSigned(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentErrorMessage), s => s.HasConsentError ? s.ConsentErrorMessage : null));
        AddSigned(Bar(WinoInfoBarSeverity.Warning, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionPending ? Translator.WinoAccount_IntelligenceConsentDeletionPending : null));
        AddSigned(Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionFailed ? Translator.WinoAccount_IntelligenceConsentDeletionFailed : null));
        AddSigned(MailboxesExpander());
        AddSigned(Bind.Visible(TranslationsExpander(), vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess));
        AddContent(Bind.Visible(signedIn, vm, nameof(vm.IsSignedIn), s => s.IsSignedIn));

        // A snapshot apply clears and refills the mailbox collection; rebuild the rows once per batch.
        Bind.Collection(vm.IntelligenceMailboxes, QueueMailboxRebuild);
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, _ => QueueMailboxRebuild());
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        try { await ViewModel.InitializeAsync(mode, parameter); }
        finally { await Dispatcher.ExecuteOnUIThread(Reveal); }
    }

    /// <summary>Replaces the first-load spinner with the page content, once.</summary>
    private void Reveal()
    {
        if (_revealed || Bindings.IsDisposed) return;
        _revealed = true;
        _loadingSpinner?.StopAnimating();
        if (_loading is not null) _loading.Hidden = true;
        _content.Hidden = false;
    }

    /// <summary>Adds a section to the content stack (hidden during the first load) at the content width.</summary>
    private T AddContent<T>(T view) where T : UIView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        _content.AddArrangedSubview(view);
        return view;
    }

    private void QueueMailboxRebuild()
    {
        if (_mailboxRebuildQueued) return;
        _mailboxRebuildQueued = true;
        BeginInvokeOnMainThread(() =>
        {
            _mailboxRebuildQueued = false;
            if (!Bindings.IsDisposed) RebuildMailboxes();
        });
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
        EventHandler handler = (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
        bar.ActionInvoked += handler;
        Bindings.Own(new ActionDisposable(() => bar.ActionInvoked -= handler));
        return bar;
    }

    private static UILabel Strong(string text) => WinoStyle.Label(text, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);

    private static UILabel Secondary(string text, UIFont? font = null)
        => WinoStyle.Label(text, font ?? WinoStyle.Description, WinoStyle.SecondaryText, 0);

    private UIButton LearnMoreButton()
    {
        var learn = IntelligenceViews.LinkButton(Translator.Buttons_LearnMore);
        Bind.OnActivated(learn, () => IntelligenceViews.OpenUrl(LearnMoreUrl));
        return learn;
    }

    /// <summary>Signed out: illustration, promotion text, the account note and Create account / Sign in.</summary>
    private UIView SignedOutPromo()
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustrationView(72);
        var title = Strong(Translator.WinoIntelligence_PromotionTitle);
        var description = Secondary(Translator.WinoIntelligence_PromotionDescription);
        var text = WinoLayout.VStack(4, title, description);
        text.Alignment = UIStackViewAlignment.Fill;
        var note = Secondary(Translator.WinoIntelligence_AccountRequired, WinoStyle.Caption);
        var buttons = Row(Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand, primary: true), Bind.Button(Translator.Buttons_SignIn, vm.SignInCommand), WinoLayout.Spacer());
        var column = WinoLayout.VStack(12, art, text, note, buttons);
        column.Alignment = UIStackViewAlignment.Fill;
        return IntelligenceViews.Surface(WrapArt(column, art));
    }

    /// <summary>Keeps the fixed-size illustration at its size inside a fill-aligned column.</summary>
    private static UIView WrapArt(UIStackView column, UIView art)
    {
        var index = Array.IndexOf(column.ArrangedSubviews, art);
        if (index < 0) return column;
        column.RemoveArrangedSubview(art);
        art.RemoveFromSuperview();
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(art);
        NSLayoutConstraint.ActivateConstraints(
        [
            art.LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor),
            art.TopAnchor.ConstraintEqualTo(host.TopAnchor),
            art.BottomAnchor.ConstraintEqualTo(host.BottomAnchor)
        ]);
        column.InsertArrangedSubview(host, (nuint)index);
        return column;
    }

    /// <summary>Not subscribed: illustration, pitch, feature chips, local note, then Get / Learn more and the price note.</summary>
    private UIView NotSubscribedPromo()
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustrationView(72);
        var title = Strong(Translator.WinoAccount_Management_AiPackPromoTitle);
        var description = Secondary(Translator.WinoAccount_Management_AiPackPromoDescription);
        var text = WinoLayout.VStack(4, title, description);
        text.Alignment = UIStackViewAlignment.Fill;
        var chips = IntelligenceViews.Flow(
        [
            WinoIntelligenceBrand.Chip(WinoIconGlyph.DailyBriefing, Translator.WinoAccount_Management_AiPackFeatureBriefing),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Note, Translator.WinoAccount_Management_AiPackFeatureSummarize),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Translate, Translator.WinoAccount_Management_AiPackFeatureTranslate),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Edit, Translator.WinoAccount_Management_AiPackFeatureRewrite),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Tag, Translator.WinoAccount_Management_AiPackFeatureLabels),
        ], 8);
        var note = Secondary(Translator.WinoAccount_Management_AiPackLocalNote, WinoStyle.Caption);

        var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        get.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var price = Secondary(Translator.WinoAccount_Management_AiPackPriceAtCheckout, WinoStyle.Caption);
        price.TextAlignment = UITextAlignment.Center;
        var learn = LearnMoreButton();
        var learnRow = WinoLayout.HStack(0, WinoLayout.Spacer(), learn, WinoLayout.Spacer());
        learnRow.Distribution = UIStackViewDistribution.EqualCentering;

        var column = WinoLayout.VStack(12, art, text, chips, note, get, learnRow, price);
        column.Alignment = UIStackViewAlignment.Fill;
        column.SetCustomSpacing(16, note);
        column.SetCustomSpacing(4, get);
        column.SetCustomSpacing(0, learnRow);
        return IntelligenceViews.Surface(WrapArt(column, art));
    }

    /// <summary>Subscription and AI credits, with the "Available quota" button that opens the usage flyout.</summary>
    private UIView SubscriptionCard()
    {
        var vm = ViewModel;
        var quota = SettingsBinder.CreateButton(Translator.WinoIntelligence_AvailableQuota, icon: WinoIconGlyph.ChevronDown);
        if (quota.Configuration is { } configuration)
        {
            configuration.ImagePlacement = NSDirectionalRectEdge.Trailing;
            quota.Configuration = configuration;
        }
        quota.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        Bind.Enabled(quota, vm, nameof(vm.IsIntelligenceUsageAvailable), s => s.IsIntelligenceUsageAvailable);
        Bind.OnActivated(quota, () => IntelligenceQuotaViewController.Present(this, quota, vm.IntelligenceUsageItems.ToList(), vm.IntelligenceResetText));
        var card = StackedCard(Translator.WinoIntelligence_SubscriptionTitle, null, WinoIconGlyph.None, Row(quota, WinoLayout.Spacer()));
        card.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        Bind.Bind(vm, nameof(vm.AiPackRenewalOrCancellationText), s => s.AiPackRenewalOrCancellationText, text => card.Description = text);
        return Bind.Visible(card, vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess);
    }

    /// <summary>Mailboxes: the account-wide consent switch, then one row per mailbox while consent is granted.</summary>
    private UIView MailboxesExpander()
    {
        var vm = ViewModel;
        var busy = IntelligenceViews.Spinner();
        var consent = new WinoTextSwitch(Translator.WinoAccount_Allowed, Translator.WinoAccount_NotAllowed, Translator.WinoIntelligence_MailboxesTitle);
        _consent = consent;
        var content = Row(busy, consent);
        var expander = new WinoSettingsExpander(Translator.WinoIntelligence_MailboxesTitle, null, WinoIconGlyph.None, content);
        expander.HeaderCard.LeadingView = new BrandIconView(WinoIconGlyph.Mail, 20);
        Bind.Bind(vm, nameof(vm.IntelligenceConsentStatusText), s => s.IntelligenceConsentStatusText, text => expander.HeaderCard.Description = text);
        Bind.Bind(vm, nameof(vm.IsConsentBusy), s => s.IsConsentBusy, on => { IntelligenceViews.SetSpinning(busy, on); consent.IsEnabled = !on; });
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, granted =>
        {
            if (!_applyingConsent) consent.IsOn = granted;
            expander.IsExpanded = granted;
        });
        EventHandler toggled = async (_, _) => await ConsentToggledAsync();
        consent.Toggled += toggled;
        Bindings.Own(new ActionDisposable(() => consent.Toggled -= toggled));

        var header = Secondary(Translator.WinoIntelligence_MailboxesDescription);
        var dataError = Bar(WinoInfoBarSeverity.Error, nameof(vm.IntelligenceDataError), s => s.IntelligenceDataError);
        var top = WinoLayout.VStack(8, header, dataError);
        top.Alignment = UIStackViewAlignment.Fill;
        var topHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(top, topHost, 12, 16, 12, 16);
        _mailboxRows.Alignment = UIStackViewAlignment.Fill;
        var items = WinoLayout.VStack(0, topHost, _mailboxRows);
        items.Alignment = UIStackViewAlignment.Fill;
        expander.Add(items, 0, 0, 0, 0);
        return expander;
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

    private void RebuildMailboxes()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        IntelligenceViews.Clear(_mailboxRows);
        if (!ViewModel.IsConsentGranted) return;

        foreach (var item in ViewModel.IntelligenceMailboxes.ToList())
        {
            _mailboxRows.AddArrangedSubview(new WinoSeparator { Fill = WinoSettingsStyle.CardStroke });
            _mailboxRows.AddArrangedSubview(MailboxRow(item, rows));
        }
    }

    /// <summary>
    /// Picture, address and summary, then Indexing enabled, Manage and Delete (Windows MailboxTemplate). The actions
    /// wrap under the address on phone width; the Mac tooltips become accessibility hints.
    /// </summary>
    private UIView MailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows)
    {
        var icon = new WinoAccountIconView(28)
        {
            Account = item.Account is not null
                ? MailAccountIconInfoFactory.Create(item.Account, pictures)
                : MailAccountIconInfoFactory.CreateProviderFallback(item.ProviderType, item.SpecialProvider)
        };
        var address = WinoStyle.Label(item.Address, WinoStyle.Body, WinoStyle.PrimaryText);
        address.LineBreakMode = UILineBreakMode.MiddleTruncation;
        var summary = WinoStyle.Label(item.IntelligenceSummary, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var text = WinoLayout.VStack(1, address, summary);
        text.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Priorities(text, UILayoutConstraintAxis.Horizontal, 1, 250);
        foreach (var label in new[] { address, summary }) WinoLayout.Priorities(label, UILayoutConstraintAxis.Horizontal, 1, 250);
        var identity = WinoLayout.HStack(12, icon, text);

        var actions = new List<UIView>();
        if (item.CanManage)
        {
            var enabled = IntelligenceViews.CheckButton(Translator.WinoIntelligence_MailboxEnabled);
            WinoAccessibility.Label(enabled, Translator.WinoIntelligence_EnableMailbox);
            WinoAccessibility.Help(enabled, item.ToggleUnavailableTooltip);
            rows.Bind(item, nameof(item.IsEnabled), i => i.IsEnabled, on => IntelligenceViews.SetChecked(enabled, on));
            rows.Enabled(enabled, item, nameof(item.CanChangeEnabled), i => i.CanChangeEnabled);
            rows.OnActivated(enabled, () =>
            {
                IntelligenceViews.SetChecked(enabled, item.IsEnabled);
                if (item.ToggleEnabledCommand?.CanExecute(item) == true) item.ToggleEnabledCommand.Execute(item);
            });
            actions.Add(enabled);
        }
        actions.Add(WinoLayout.Spacer());
        var manage = SettingsBinder.CreateButton(Translator.Buttons_Manage);
        manage.Enabled = item.CanManage;
        WinoAccessibility.Help(manage, item.ManageUnavailableTooltip);
        rows.OnActivated(manage, () => { if (item.ManageCommand?.CanExecute(item) == true) item.ManageCommand.Execute(item); });
        actions.Add(manage);
        if (item.HasServerIntelligence)
        {
            var delete = SettingsBinder.CreateButton(Translator.Buttons_Delete);
            if (delete.Configuration is { } configuration)
            {
                configuration.BaseForegroundColor = WinoStyle.Critical;
                delete.Configuration = configuration;
            }
            rows.Enabled(delete, item, nameof(item.IsDeleting), i => !i.IsDeleting);
            rows.OnActivated(delete, () => { if (item.DeleteCommand?.CanExecute(item) == true) item.DeleteCommand.Execute(item); });
            actions.Add(delete);
        }
        var actionRow = WinoLayout.HStack(8, actions.ToArray());

        var stack = WinoLayout.VStack(6, identity, actionRow);
        stack.Alignment = UIStackViewAlignment.Fill;
        var host = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(stack, host, 8, 16, 8, 16);
        return host;
    }

    /// <summary>Translations: the default translation and summary languages.</summary>
    private UIView TranslationsExpander()
    {
        var vm = ViewModel;
        var translate = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedDefaultTranslationLanguage), s => s.SelectedDefaultTranslationLanguage, (s, v) => s.SelectedDefaultTranslationLanguage = v);
        var summarize = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedSummarizeLanguage), s => s.SelectedSummarizeLanguage, (s, v) => s.SelectedSummarizeLanguage = v);
        return Expander(Translator.SettingsAppPreferences_AiActions_Title, Translator.SettingsAppPreferences_AiActions_Description, WinoIconGlyph.Translate, null,
            StackedCard(Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Title, Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Description, WinoIconGlyph.None, Row(translate, WinoLayout.Spacer())),
            StackedCard(Translator.SettingsAppPreferences_AiSummarizeLanguage_Title, Translator.SettingsAppPreferences_AiSummarizeLanguage_Description, WinoIconGlyph.None, Row(summarize, WinoLayout.Spacer())));
    }
}
