using System.Windows.Input;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.Widget;
using Google.Android.Material.CheckBox;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Bindings;
using ActionDisposable = Wino.Mail.Android.Infrastructure.ActionDisposable;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// Wino Intelligence hub (Windows WinoIntelligencePage, iOS WinoIntelligencePageViewController; shared
/// WinoAccountManagementPageViewModel): the signed-out and not-subscribed promotions, the subscription card with the
/// quota sheet, consent and mailbox rows, and the translation languages. The Mac side-by-side promotions stack
/// vertically on phone width. Android v1 sells nothing in the app (IPlatformCapabilities.MicrosoftStore and
/// AppleAppStore are false), so the not-subscribed promotion has no Get button or price note.
/// </summary>
public sealed class WinoIntelligencePageFragment : SettingsPageFragment<WinoAccountManagementPageViewModel>
{
    private IPictureStorageService? _pictures;
    private IMailDialogService? _dialogs;
    private bool _sellsAddOns;
    private LinearLayout? _mailboxRows;
    private WinoTextSwitch? _consent;
    private LinearLayout? _content;
    private View? _loading;
    private BindingScope? _rowScope;
    private bool _applyingConsent;
    private bool _revealed;
    private bool _mailboxRebuildQueued;

    protected override void BuildPage()
    {
        var vm = VM;
        var services = MainApplication.Services;
        _pictures = services?.GetService<IPictureStorageService>();
        _dialogs = services?.GetService<IMailDialogService>();
        var capabilities = services?.GetService<IPlatformCapabilities>();
        _sellsAddOns = capabilities?.Purchases == true;
        _revealed = false;
        _applyingConsent = false;
        _mailboxRebuildQueued = false;

        // First load: the page starts with default ViewModel state (signed out, not subscribed, no mailboxes). Showing
        // it would flash the promotions before the cached account state lands, so the content stays hidden behind one
        // spinner until the cached state is applied.
        _loading = Add(IntelligenceViews.LoadingState(Ctx));
        var content = Stack(Resource.Dimension.wino_space_m);
        content.Visibility = ViewStates.Gone;
        _content = content;
        Add(content);
        Bindings.Own(new ActionDisposable(() =>
        {
            _rowScope?.Dispose();
            _rowScope = null;
            _content = null;
            _loading = null;
            _mailboxRows = null;
            _consent = null;
        }));

        // Status rows shared by every state. The network refresh row appears only when the refresh is slow, so a quick
        // refresh does not push the cards down and back up.
        var spinner = IntelligenceViews.Spinner(Ctx);
        var updating = Row(spinner, Text(Translator.WinoIntelligence_Updating));
        var showUpdating = IntelligenceViews.DelayedProgressRow(updating, spinner, () => !Bindings.IsDisposed);
        Bind.Bind(vm, nameof(vm.IsIntelligenceRefreshing), s => s.IsIntelligenceRefreshing, on =>
        {
            // The refresh starts after the account state and the cached snapshot are applied.
            if (on) Reveal();
            showUpdating(on);
        });
        AddTo(content, updating, Resource.Dimension.wino_space_m);
        AddTo(content, CommandBar(WinoInfoBarSeverity.Informational, nameof(vm.PurchaseStatusMessage), s => s.PurchaseStatusMessage,
            Translator.WinoAccount_Management_RefreshPurchases, vm.RefreshPurchasesCommand), Resource.Dimension.wino_space_m);
        // Windows: the refresh error bar is IsClosable="True"; the purchase status bar is not closable.
        var refreshError = CommandBar(WinoInfoBarSeverity.Warning, nameof(vm.IntelligenceRefreshError), s => s.IntelligenceRefreshError,
            Translator.Buttons_Retry, vm.RetryIntelligenceRefreshCommand);
        refreshError.IsClosable = true;
        AddTo(content, refreshError, Resource.Dimension.wino_space_m);

        AddTo(content, Bind.Visible(SignedOutPromo(), vm, nameof(vm.IsSignedIn), s => s.IsSignedOut), Resource.Dimension.wino_space_m);

        var promo = NotSubscribedPromo();
        Bind.Bind(vm.AiPackAddOn, nameof(WinoAddOnItemViewModel.IsPurchased), a => a.IsNotPurchased,
            show => promo.Visibility = show ? ViewStates.Visible : ViewStates.Gone);
        var signedIn = Stack(Resource.Dimension.wino_space_m,
            promo,
            SubscriptionCard(),
            Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentErrorMessage), s => s.HasConsentError ? s.ConsentErrorMessage : null),
            Bar(WinoInfoBarSeverity.Warning, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionPending ? Translator.WinoAccount_IntelligenceConsentDeletionPending : null),
            Bar(WinoInfoBarSeverity.Error, nameof(vm.ConsentDataDeletionStatus), s => s.IsConsentDeletionFailed ? Translator.WinoAccount_IntelligenceConsentDeletionFailed : null),
            MailboxesExpander(),
            Bind.Visible(TranslationsExpander(), vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess));
        AddTo(content, Bind.Visible(signedIn, vm, nameof(vm.IsSignedIn), s => s.IsSignedIn), Resource.Dimension.wino_space_m);

        // A snapshot apply clears and refills the mailbox collection; rebuild the rows once per batch.
        Bind.Collection(vm.IntelligenceMailboxes, QueueMailboxRebuild);
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, _ => QueueMailboxRebuild());

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

    private void QueueMailboxRebuild()
    {
        if (_mailboxRebuildQueued) return;
        _mailboxRebuildQueued = true;
        var scope = Bindings;
        Page.Post(() =>
        {
            _mailboxRebuildQueued = false;
            if (scope.IsDisposed) return;
            try { RebuildMailboxes(); }
            catch (Exception exception) { ReportError(exception); }
        });
    }

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

    private View LearnMoreButton()
    {
        var learn = IntelligenceViews.LinkButton(Ctx, Translator.Buttons_LearnMore);
        Bind.Views.Click(learn, () => IntelligenceViews.OpenUrl(WinoAccountManagementPageFragment.IntelligenceLearnMoreUrl));
        var row = new FrameLayout(Ctx);
        row.AddView(learn, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterHorizontal));
        return row;
    }

    /// <summary>The fixed-size illustration at the start of a column.</summary>
    private View Art()
    {
        var host = new FrameLayout(Ctx);
        host.AddView(new WinoIntelligencePromotionIllustrationView(Ctx, Resource.Dimension.wino_winoaccount_promo_art), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Start));
        return host;
    }

    /// <summary>Signed out: illustration, promotion text, the account note and Create account / Sign in.</summary>
    private View SignedOutPromo()
    {
        var vm = VM;
        var text = Stack(Resource.Dimension.wino_space_2xs, Text(Translator.WinoIntelligence_PromotionTitle, WinoTextStyle.BodyStrong), Text(Translator.WinoIntelligence_PromotionDescription, WinoTextStyle.Description));
        var note = Text(Translator.WinoIntelligence_AccountRequired, WinoTextStyle.Caption);
        var buttons = IntelligenceViews.Flow(Ctx, [Bind.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand, primary: true), Bind.Button(Translator.Buttons_SignIn, vm.SignInCommand)], Resource.Dimension.wino_space_s);
        return IntelligenceViews.Surface(Ctx, Stack(Resource.Dimension.wino_space_m, Art(), text, note, buttons));
    }

    /// <summary>Not subscribed: illustration, pitch, feature chips, local note, then Get / Learn more and the price note where the platform sells it.</summary>
    private View NotSubscribedPromo()
    {
        var vm = VM;
        var text = Stack(Resource.Dimension.wino_space_2xs, Text(Translator.WinoAccount_Management_AiPackPromoTitle, WinoTextStyle.BodyStrong), Text(Translator.WinoAccount_Management_AiPackPromoDescription, WinoTextStyle.Description));
        var note = Text(Translator.WinoAccount_Management_AiPackLocalNote, WinoTextStyle.Caption);
        var column = Stack(Resource.Dimension.wino_space_m, Art(), text, WinoAccountManagementPageFragment.FeatureChips(Ctx), note);
        if (_sellsAddOns)
        {
            var get = Bind.Button(Translator.WinoAccount_Management_AiPackGetButton, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, primary: true);
            AddTo(column, get, Resource.Dimension.wino_space_l);
            AddTo(column, LearnMoreButton(), Resource.Dimension.wino_space_2xs);
            var price = Text(Translator.WinoAccount_Management_AiPackPriceAtCheckout, WinoTextStyle.Caption);
            price.Gravity = GravityFlags.CenterHorizontal;
            AddTo(column, price, 0);
        }
        else AddTo(column, LearnMoreButton(), Resource.Dimension.wino_space_m);
        return IntelligenceViews.Surface(Ctx, column);
    }

    /// <summary>Subscription and AI credits, with the "Available quota" button that opens the usage sheet.</summary>
    private View SubscriptionCard()
    {
        var vm = VM;
        var quota = SettingsBinder.CreateButton(Ctx, Translator.WinoIntelligence_AvailableQuota, icon: WinoIconGlyph.ChevronDown);
        quota.IconGravity = Google.Android.Material.Button.MaterialButton.IconGravityTextEnd;
        Bind.Views.Enabled(quota, vm, nameof(vm.IsIntelligenceUsageAvailable), s => s.IsIntelligenceUsageAvailable);
        Bind.Views.Click(quota, () => IntelligenceQuotaSheet.Present(Ctx, vm.IntelligenceUsageItems.ToList(), vm.IntelligenceResetText));
        var card = StackedCard(Translator.WinoIntelligence_SubscriptionTitle, null, WinoIconGlyph.None, Row(quota));
        card.LeadingView = new BrandIconView(Ctx, WinoIconGlyph.WinoIntelligence, Resource.Dimension.wino_glyph_l);
        Bind.Bind(vm, nameof(vm.AiPackRenewalOrCancellationText), s => s.AiPackRenewalOrCancellationText, text => card.Description = text);
        return Bind.Visible(card, vm, nameof(vm.HasIntelligenceAccess), s => s.HasIntelligenceAccess);
    }

    /// <summary>Mailboxes: the account-wide consent switch, then one row per mailbox while consent is granted.</summary>
    private View MailboxesExpander()
    {
        var vm = VM;
        var busy = IntelligenceViews.Spinner(Ctx);
        var consent = new WinoTextSwitch(Ctx, Translator.WinoAccount_Allowed, Translator.WinoAccount_NotAllowed, Translator.WinoIntelligence_MailboxesTitle);
        _consent = consent;
        var expander = new WinoSettingsExpander(Ctx, Translator.WinoIntelligence_MailboxesTitle, null, WinoIconGlyph.None, Row(busy, consent));
        expander.HeaderCard.LeadingView = new BrandIconView(Ctx, WinoIconGlyph.Mail, Resource.Dimension.wino_glyph_l);
        Bind.Bind(vm, nameof(vm.IntelligenceConsentStatusText), s => s.IntelligenceConsentStatusText, text => expander.HeaderCard.Description = text);
        Bind.Bind(vm, nameof(vm.IsConsentBusy), s => s.IsConsentBusy, on =>
        {
            IntelligenceViews.SetSpinning(busy, on);
            consent.IsEnabled = !on;
        });
        Bind.Bind(vm, nameof(vm.IsConsentGranted), s => s.IsConsentGranted, granted =>
        {
            if (!_applyingConsent) consent.IsOn = granted;
            expander.IsExpanded = granted;
        });
        EventHandler toggled = async (_, _) => await ConsentToggledAsync();
        consent.Toggled += toggled;
        Bindings.Own(new ActionDisposable(() => consent.Toggled -= toggled));

        var header = Text(Translator.WinoIntelligence_MailboxesDescription, WinoTextStyle.Description);
        var dataError = Bar(WinoInfoBarSeverity.Error, nameof(vm.IntelligenceDataError), s => s.IntelligenceDataError);
        var top = Stack(Resource.Dimension.wino_space_s, header, dataError);
        WinoTokens.Pad(top, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_m, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_m);
        var rows = Stack(0);
        _mailboxRows = rows;
        expander.Add(Stack(0, top, rows), 0, 0, 0, 0);
        return expander;
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

    private void RebuildMailboxes()
    {
        if (_mailboxRows is not { } stack) return;
        _rowScope?.Dispose();
        _rowScope = new BindingScope();
        var rows = Bind.Child(_rowScope);
        stack.RemoveAllViews();
        if (!VM.IsConsentGranted) return;

        foreach (var item in VM.IntelligenceMailboxes.ToList())
        {
            AddTo(stack, IntelligenceViews.Separator(Ctx), 0);
            AddTo(stack, MailboxRow(item, rows), 0);
        }
    }

    /// <summary>
    /// Picture, address and summary, then Indexing enabled, Manage and Delete (Windows MailboxTemplate). The actions
    /// wrap under the address on phone width; the Mac tooltips become long-press tooltips.
    /// </summary>
    private View MailboxRow(WinoIntelligenceMailboxItemViewModel item, SettingsBinder rows)
    {
        var context = Ctx;
        var icon = new WinoAccountIconView(context)
        {
            Size = WinoTokens.Dp(context, Resource.Dimension.wino_winoaccount_mailbox_icon),
            Account = item.Account is not null && _pictures is not null
                ? MailAccountIconInfoFactory.Create(item.Account, _pictures)
                : MailAccountIconInfoFactory.CreateProviderFallback(item.ProviderType, item.SpecialProvider)
        };
        var address = Text(item.Address);
        address.SetSingleLine(true);
        address.Ellipsize = global::Android.Text.TextUtils.TruncateAt.Middle;
        var summary = Text(item.IntelligenceSummary, WinoTextStyle.Description);
        var identity = IntelligenceViews.HStack(context, Resource.Dimension.wino_space_m, null, icon);
        identity.AddView(Stack(Resource.Dimension.wino_space_hairline, address, summary), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_m) });

        var actions = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        actions.SetGravity(GravityFlags.CenterVertical);
        if (item.CanManage)
        {
            var enabled = new MaterialCheckBox(context) { Text = Translator.WinoIntelligence_MailboxEnabled, ContentDescription = Translator.WinoIntelligence_EnableMailbox };
            enabled.SetTextColor(WinoColors.PrimaryText(context));
            WinoMaterialAccent.Tint(enabled);
            if (!string.IsNullOrWhiteSpace(item.ToggleUnavailableTooltip)) TooltipCompat.SetTooltipText(enabled, item.ToggleUnavailableTooltip);
            rows.Bind(item, nameof(item.IsEnabled), i => i.IsEnabled, on => { if (enabled.Checked != on) enabled.Checked = on; });
            rows.Views.Enabled(enabled, item, nameof(item.CanChangeEnabled), i => i.CanChangeEnabled);
            rows.Views.Click(enabled, () =>
            {
                // The command owns the state; the box shows it again until the change lands.
                enabled.Checked = item.IsEnabled;
                if (item.ToggleEnabledCommand?.CanExecute(item) == true) item.ToggleEnabledCommand.Execute(item);
            });
            actions.AddView(enabled);
        }
        actions.AddView(IntelligenceViews.Spacer(context), new LinearLayout.LayoutParams(0, 1, 1));
        var manage = SettingsBinder.CreateButton(context, Translator.Buttons_Manage);
        manage.Enabled = item.CanManage;
        if (!string.IsNullOrWhiteSpace(item.ManageUnavailableTooltip)) TooltipCompat.SetTooltipText(manage, item.ManageUnavailableTooltip);
        rows.Views.Click(manage, () => { if (item.ManageCommand?.CanExecute(item) == true) item.ManageCommand.Execute(item); });
        actions.AddView(manage);
        if (item.HasServerIntelligence)
        {
            var delete = SettingsBinder.CreateButton(context, Translator.Buttons_Delete);
            delete.SetTextColor(WinoColors.Critical(context));
            rows.Views.Enabled(delete, item, nameof(item.IsDeleting), i => !i.IsDeleting);
            rows.Views.Click(delete, () => { if (item.DeleteCommand?.CanExecute(item) == true) item.DeleteCommand.Execute(item); });
            actions.AddView(delete, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
        }

        var stack = Stack(Resource.Dimension.wino_space_xs, identity, actions);
        WinoTokens.Pad(stack, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_s, Resource.Dimension.wino_space_l, Resource.Dimension.wino_space_s);
        return stack;
    }

    /// <summary>Translations: the default translation and summary languages.</summary>
    private View TranslationsExpander()
    {
        var vm = VM;
        var translate = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedDefaultTranslationLanguage), s => s.SelectedDefaultTranslationLanguage, (s, v) => s.SelectedDefaultTranslationLanguage = v);
        var summarize = Bind.PopUp<WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm, s => s.AvailableAiLanguages, o => o.Label,
            nameof(vm.SelectedSummarizeLanguage), s => s.SelectedSummarizeLanguage, (s, v) => s.SelectedSummarizeLanguage = v);
        return Expander(Translator.SettingsAppPreferences_AiActions_Title, Translator.SettingsAppPreferences_AiActions_Description, WinoIconGlyph.Translate, null,
            StackedCard(Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Title, Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Description, WinoIconGlyph.None, Row(translate)),
            StackedCard(Translator.SettingsAppPreferences_AiSummarizeLanguage_Title, Translator.SettingsAppPreferences_AiSummarizeLanguage_Description, WinoIconGlyph.None, Row(summarize)));
    }
}
