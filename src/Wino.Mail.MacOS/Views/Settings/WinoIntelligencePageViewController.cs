using System.Windows.Input;
using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Wino Intelligence hub (Windows WinoIntelligencePage, shared WinoAccountManagementPageViewModel):
/// the signed-out and not-subscribed promotions, the subscription card with the quota flyout,
/// consent and mailbox rows, and the translation languages.
/// </summary>
public sealed class WinoIntelligencePageViewController(WinoAccountManagementPageViewModel viewModel, IPictureStorageService pictures,
    IMailDialogService dialogs, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<WinoAccountManagementPageViewModel>(viewModel, dispatcher, logger)
{
    private const string LearnMoreUrl = "https://www.winomail.app/intelligence";
    private readonly NSStackView _mailboxRows = WinoLayout.VStack(0);
    private WinoTextSwitch? _consent;
    private readonly NSStackView _content = WinoLayout.VStack(12);
    private NSView? _loading;
    private NSProgressIndicator? _loadingSpinner;
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
        _content.Alignment = NSLayoutAttribute.Leading;
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
        signedIn.Alignment = NSLayoutAttribute.Leading;
        void AddSigned(NSView view)
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            signedIn.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(signedIn.WidthAnchor).Active = true;
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
#if DEBUG
        WinoIntelligenceDebug.Current = vm;
#endif
    }

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        try { await ViewModel.InitializeAsync(mode, parameter!); }
        finally { await Dispatcher.ExecuteOnUIThread(Reveal); }
    }

    /// <summary>Replaces the first-load spinner with the page content, once.</summary>
    private void Reveal()
    {
        if (_revealed || Bindings.IsDisposed) return;
        _revealed = true;
        _loadingSpinner?.StopAnimation(null);
        if (_loading is not null) _loading.Hidden = true;
        _content.Hidden = false;
    }

    /// <summary>Adds a section to the content stack (hidden during the first load) at the content width.</summary>
    private T AddContent<T>(T view) where T : NSView
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        view.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        if (view is NSStackView stack) stack.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        _content.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(_content.WidthAnchor).Active = true;
        return view;
    }

    private void QueueMailboxRebuild()
    {
        if (_mailboxRebuildQueued) return;
        _mailboxRebuildQueued = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
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
        bar.ActionInvoked += (_, _) => { if (command.CanExecute(null)) command.Execute(null); };
        return bar;
    }

    private static NSTextField Strong(string text) => WinoStyle.Label(text, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);

    private static NSTextField Secondary(string text, double size = 12, double maxWidth = 0)
    {
        var label = WinoStyle.Label(text, NSFont.SystemFontOfSize((nfloat)size), WinoStyle.SecondaryText, 0);
        if (maxWidth > 0) label.WidthAnchor.ConstraintLessThanOrEqualTo((nfloat)maxWidth).Active = true;
        return label;
    }

    /// <summary>Signed out: illustration, promotion text, the account note and Create account / Sign in.</summary>
    private NSView SignedOutPromo()
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustrationView();
        var title = Strong(Translator.WinoIntelligence_PromotionTitle);
        var description = Secondary(Translator.WinoIntelligence_PromotionDescription, maxWidth: 640);
        var text = WinoLayout.VStack(4, title, description);
        text.Alignment = NSLayoutAttribute.Leading;
        var note = Secondary(Translator.WinoIntelligence_AccountRequired, 11);
        var buttons = Row(Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand, primary: true), Bind.Button(Translator.Buttons_SignIn, vm.SignInCommand));
        var column = WinoLayout.VStack(12, text, note, buttons);
        column.Alignment = NSLayoutAttribute.Leading;
        column.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var row = WinoLayout.HStack(20, art, column);
        row.Alignment = NSLayoutAttribute.Top;
        return IntelligenceViews.Surface(row);
    }

    /// <summary>Not subscribed: illustration, pitch, feature chips, local note, and the Get / Learn more column.</summary>
    private NSView NotSubscribedPromo()
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustrationView();
        var title = Strong(Translator.WinoAccount_Management_AiPackPromoTitle);
        var description = Secondary(Translator.WinoAccount_Management_AiPackPromoDescription, maxWidth: 520);
        var text = WinoLayout.VStack(4, title, description);
        text.Alignment = NSLayoutAttribute.Leading;
        var chips = IntelligenceViews.Flow(
        [
            WinoIntelligenceBrand.Chip(WinoIconGlyph.DailyBriefing, Translator.WinoAccount_Management_AiPackFeatureBriefing),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Note, Translator.WinoAccount_Management_AiPackFeatureSummarize),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Translate, Translator.WinoAccount_Management_AiPackFeatureTranslate),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Edit, Translator.WinoAccount_Management_AiPackFeatureRewrite),
            WinoIntelligenceBrand.Chip(WinoIconGlyph.Tag, Translator.WinoAccount_Management_AiPackFeatureLabels),
        ], 8, 420);
        var note = Secondary(Translator.WinoAccount_Management_AiPackLocalNote, 11);
        var column = WinoLayout.VStack(12, text, chips, note);
        column.Alignment = NSLayoutAttribute.Leading;
        column.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        note.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;

        var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
        var learn = new NSButton { Bordered = false, TranslatesAutoresizingMaskIntoConstraints = false };
        learn.AttributedTitle = new NSAttributedString(Translator.Buttons_LearnMore, new NSStringAttributes { ForegroundColor = WinoStyle.Accent, Font = NSFont.SystemFontOfSize(12) });
        Bind.OnActivated(learn, () => NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl(LearnMoreUrl)));
        var price = Secondary(Translator.WinoAccount_Management_AiPackPriceAtCheckout, 11);
        price.Alignment = NSTextAlignment.Right;
        var side = WinoLayout.VStack(8, get, learn, price);
        side.Alignment = NSLayoutAttribute.CenterX;
        WinoLayout.Size(side, 180);
        get.WidthAnchor.ConstraintEqualTo(side.WidthAnchor).Active = true;
        price.WidthAnchor.ConstraintEqualTo(side.WidthAnchor).Active = true;

        var row = WinoLayout.HStack(20, art, column, side);
        row.Alignment = NSLayoutAttribute.CenterY;
        return IntelligenceViews.Surface(row);
    }

    /// <summary>Subscription and AI credits, with the "Available quota" pull-down that opens the usage flyout.</summary>
    private NSView SubscriptionCard()
    {
        var vm = ViewModel;
        var quota = SettingsBinder.CreateButton(Translator.WinoIntelligence_AvailableQuota, icon: WinoIconGlyph.ChevronDown);
        quota.ImagePosition = NSCellImagePosition.ImageTrailing;
        Bind.Enabled(quota, vm, nameof(vm.IsIntelligenceUsageAvailable), s => s.IsIntelligenceUsageAvailable);
        Bind.OnActivated(quota, () =>
        {
            var popover = new NSPopover
            {
                Behavior = NSPopoverBehavior.Transient,
                Animates = true,
                ContentViewController = new IntelligenceQuotaViewController(vm.IntelligenceUsageItems.ToList(), vm.IntelligenceResetText)
            };
            popover.Show(quota.Bounds, quota, NSRectEdge.MaxYEdge);
        });
#if DEBUG
        WinoIntelligenceDebug.OpenQuota = () => quota.PerformClick(quota);
#endif
        var card = Card(Translator.WinoIntelligence_SubscriptionTitle, null, WinoIconGlyph.None, quota);
        card.LeadingView = new BrandIconView(WinoIconGlyph.WinoIntelligence, 20);
        Bind.Bind(vm, nameof(vm.AiPackRenewalOrCancellationText), s => s.AiPackRenewalOrCancellationText, text => card.Description = text);
        return Bind.Visible(card, vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess);
    }

    /// <summary>Mailboxes: the account-wide consent switch, then one row per mailbox while consent is granted.</summary>
    private NSView MailboxesExpander()
    {
        var vm = ViewModel;
        var busy = IntelligenceViews.Spinner();
        _consent = new WinoTextSwitch(Translator.WinoAccount_Allowed, Translator.WinoAccount_NotAllowed, Translator.WinoIntelligence_MailboxesTitle);
        var content = Row(busy, _consent);
        var expander = new WinoSettingsExpander(Translator.WinoIntelligence_MailboxesTitle, null, WinoIconGlyph.None, content);
        expander.HeaderCard.LeadingView = new BrandIconView(WinoIconGlyph.Mail, 20);
        Bind.Bind(vm, nameof(vm.IntelligenceConsentStatusText), s => s.IntelligenceConsentStatusText, text => expander.HeaderCard.Description = text);
        Bind.Bind(vm, nameof(vm.IsConsentBusy), s => s.IsConsentBusy, on => { IntelligenceViews.SetSpinning(busy, on); _consent.IsEnabled = !on; });
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, granted =>
        {
            if (!_applyingConsent) _consent.IsOn = granted;
            expander.IsExpanded = granted;
        });
        _consent.Toggled += async (_, _) => await ConsentToggledAsync();

        var header = Secondary(Translator.WinoIntelligence_MailboxesDescription);
        var dataError = Bar(WinoInfoBarSeverity.Error, nameof(vm.IntelligenceDataError), s => s.IntelligenceDataError);
        var top = WinoLayout.VStack(8, header, dataError);
        top.Alignment = NSLayoutAttribute.Leading;
        header.WidthAnchor.ConstraintEqualTo(top.WidthAnchor).Active = true;
        dataError.WidthAnchor.ConstraintEqualTo(top.WidthAnchor).Active = true;
        var topHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(top, topHost, 12, 16, 12, 16);
        var items = WinoLayout.VStack(0, topHost, _mailboxRows);
        items.Alignment = NSLayoutAttribute.Leading;
        topHost.WidthAnchor.ConstraintEqualTo(items.WidthAnchor).Active = true;
        _mailboxRows.Alignment = NSLayoutAttribute.Leading;
        _mailboxRows.WidthAnchor.ConstraintEqualTo(items.WidthAnchor).Active = true;
        expander.Add(items, 0, 0, 0, 0);
        return expander;
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

    private void RebuildMailboxes()
    {
        _rowScope?.Dispose();
        _rowScope = Bindings.Own(new BindingScope());
        var rows = Bind.Child(_rowScope);
        foreach (var view in _mailboxRows.ArrangedSubviews) { _mailboxRows.RemoveArrangedSubview(view); view.RemoveFromSuperview(); }
        if (!ViewModel.IsConsentGranted) return;

        foreach (var item in ViewModel.IntelligenceMailboxes.ToList())
        {
            var separator = new WinoSeparator { Fill = WinoSettingsStyle.CardStroke };
            _mailboxRows.AddArrangedSubview(separator);
            separator.WidthAnchor.ConstraintEqualTo(_mailboxRows.WidthAnchor).Active = true;
            var row = MailboxRow(item, rows);
            _mailboxRows.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(_mailboxRows.WidthAnchor).Active = true;
        }
    }

    /// <summary>Picture, address and summary, then Indexing enabled, Manage and Delete (Windows MailboxTemplate).</summary>
    private NSView MailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows)
    {
        var icon = new WinoAccountIconView(28)
        {
            Account = item.Account is not null
                ? MailAccountIconInfoFactory.Create(item.Account, pictures)
                : MailAccountIconInfoFactory.CreateProviderFallback(item.ProviderType, item.SpecialProvider)
        };
        var address = WinoStyle.Label(item.Address, WinoStyle.Body, WinoStyle.PrimaryText);
        address.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var summary = WinoStyle.Label(item.IntelligenceSummary, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText);
        summary.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var text = WinoLayout.VStack(1, address, summary);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        foreach (var label in new[] { address, summary }) label.SetContentCompressionResistancePriority(1, NSLayoutConstraintOrientation.Horizontal);

        var views = new List<NSView> { icon, text };
        if (item.CanManage)
        {
            var enabled = WinoCheckbox.Create(Translator.WinoIntelligence_MailboxEnabled, () => { });
            enabled.Font = NSFont.SystemFontOfSize(12);
            WinoAccessibility.Label(enabled, Translator.WinoIntelligence_EnableMailbox);
            enabled.ToolTip = item.ToggleUnavailableTooltip;
            rows.Bind(item, nameof(item.IsEnabled), i => i.IsEnabled, on => enabled.State = on ? NSCellStateValue.On : NSCellStateValue.Off);
            rows.Enabled(enabled, item, nameof(item.CanChangeEnabled), i => i.CanChangeEnabled);
            rows.OnActivated(enabled, () =>
            {
                enabled.State = item.IsEnabled ? NSCellStateValue.On : NSCellStateValue.Off;
                if (item.ToggleEnabledCommand?.CanExecute(item) == true) item.ToggleEnabledCommand.Execute(item);
            });
            views.Add(enabled);
        }
        var manage = SettingsBinder.CreateButton(Translator.Buttons_Manage);
        manage.Enabled = item.CanManage;
        manage.ToolTip = item.ManageUnavailableTooltip;
        rows.OnActivated(manage, () => { if (item.ManageCommand?.CanExecute(item) == true) item.ManageCommand.Execute(item); });
        views.Add(manage);
        if (item.HasServerIntelligence)
        {
            var delete = SettingsBinder.CreateButton(Translator.Buttons_Delete);
            rows.Enabled(delete, item, nameof(item.IsDeleting), i => !i.IsDeleting);
            rows.OnActivated(delete, () => { if (item.DeleteCommand?.CanExecute(item) == true) item.DeleteCommand.Execute(item); });
            views.Add(delete);
        }
        var row = WinoLayout.HStack(16, views.ToArray());
        row.SetCustomSpacing(12, icon);
        row.EdgeInsets = new NSEdgeInsets(8, 16, 8, 16);
        return row;
    }

    /// <summary>Translations: the default translation and summary languages.</summary>
    private NSView TranslationsExpander()
    {
        var vm = ViewModel;
        var translate = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedDefaultTranslationLanguage), s => s.SelectedDefaultTranslationLanguage, (s, v) => s.SelectedDefaultTranslationLanguage = v);
        var summarize = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedSummarizeLanguage), s => s.SelectedSummarizeLanguage, (s, v) => s.SelectedSummarizeLanguage = v);
        return Expander(Translator.SettingsAppPreferences_AiActions_Title, Translator.SettingsAppPreferences_AiActions_Description, WinoIconGlyph.Translate, null,
            Card(Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Title, Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Description, WinoIconGlyph.None, translate),
            Card(Translator.SettingsAppPreferences_AiSummarizeLanguage_Title, Translator.SettingsAppPreferences_AiSummarizeLanguage_Description, WinoIconGlyph.None, summarize));
    }
}
