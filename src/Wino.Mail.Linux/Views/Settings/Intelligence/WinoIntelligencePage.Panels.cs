using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Ai;
using Wino.Mail.Controls.Linux.Intelligence;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Intelligence;

/// <summary>The offer panel (no access) and the subscribed panel (access) of the Wino Intelligence page.</summary>
internal sealed partial class WinoIntelligencePage
{
    private readonly List<Gtk.Widget> _perkRows = [];
    private readonly List<Gtk.Widget> _usageRows = [];
    private Adw.ExpanderRow? _included;
    private Adw.ExpanderRow? _status;
    private Adw.ExpanderRow? _languages;
    private Gtk.Widget? _usageUnavailable;
    private Gtk.Switch? _consent;
    private bool _applyingConsent;

    #region Offer

    private IEnumerable<WinoSettingsGroup> OfferGroups()
    {
        var hero = Page.AddGroup();
        hero.HidesWhenEmpty = false;
        hero.Root.Add(Hero());
        yield return hero;

        var included = Page.AddGroup();
        var vm = ViewModel;
        _included = WinoSettingsRows.Expander(Translator.WinoIntelligence_Included_Title, Translator.WinoIntelligence_Included_Description,
            WinoIconGlyph.WinoIntelligence, expanded: true);
        included.Add(_included);
        Settings.Collection(vm.OfferPerks, RebuildPerks);
        yield return included;

        yield return Page.AddGroup(Translator.WinoIntelligence_Locked_Caption, null,
            IntelligenceViews.LockedRow(Translator.WinoIntelligence_MailClassification_Title, Translator.WinoIntelligence_MailClassification_Description, WinoIconGlyph.Mail),
            IntelligenceViews.LockedRow(Translator.WinoIntelligence_Languages_Title, Translator.WinoIntelligence_Languages_Description, WinoIconGlyph.Translate));
    }

    /// <summary>Illustration, headline and the one primary action: Get Wino Intelligence signed out, Subscribe signed in.</summary>
    private Gtk.Widget Hero()
    {
        var vm = ViewModel;
        var art = new WinoIntelligencePromotionIllustration(96);
        Settings.Own(art);
        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.Append(IntelligenceViews.Text(Translator.WinoIntelligence_Hero_Title, "title-3"));
        text.Append(IntelligenceViews.Text(Translator.WinoIntelligence_Hero_Description, "dim-label"));

        var signedOut = ButtonRow();
        signedOut.SetMarginTop(12);
        signedOut.Append(WinoA11y.Rename(Settings.Button(Translator.WinoIntelligence_GetButton, vm.GetWinoIntelligenceCommand, suggested: true), Translator.WinoIntelligence_GetButton));
        signedOut.Append(WinoA11y.Rename(Settings.Button(Translator.Buttons_CreateAccount, vm.RegisterCommand), Translator.Buttons_CreateAccount));
        Settings.Visible(signedOut, vm, nameof(vm.IsSignedIn), static s => s.IsSignedOut);
        text.Append(signedOut);

        var signedIn = ButtonRow();
        signedIn.SetMarginTop(12);
        var subscribe = Settings.Button(vm.HeroSubscribeButtonText, vm.PurchaseAddOnCommand, () => vm.AiPackAddOn, suggested: true);
        Settings.Bind(vm, nameof(vm.HeroSubscribeButtonText), static s => s.HeroSubscribeButtonText, title =>
        {
            subscribe.SetLabel(title ?? string.Empty);
            WinoA11y.Rename(subscribe, title ?? string.Empty);
        });
        signedIn.Append(subscribe);
        var learnMore = IntelligenceViews.Link(Translator.Buttons_LearnMore);
        Settings.Own(GtkSignals.OnClicked(learnMore, () => Observe(_launcher.LaunchUriAsync(new Uri(IntelligenceViews.LearnMoreUrl)))));
        signedIn.Append(learnMore);
        Settings.Visible(signedIn, vm, nameof(vm.IsSignedIn), static s => s.IsSignedIn);
        text.Append(signedIn);

        var addOn = vm.AiPackAddOn;
        var error = IntelligenceViews.Text(null, "caption", "error");
        Settings.Bind(addOn, nameof(WinoAddOnItemViewModel.ErrorText), static a => a.ShowErrorState ? a.ErrorText : null, message =>
        {
            error.SetText(message ?? string.Empty);
            error.SetVisible(!string.IsNullOrWhiteSpace(message));
        });
        text.Append(error);
        var terms = IntelligenceViews.Text(null, "caption", "dim-label");
        terms.SetMarginTop(8);
        Settings.Bind(vm, nameof(vm.IntelligenceHeroTermsText), static s => s.IntelligenceHeroTermsText, value => terms.SetText(value ?? string.Empty));
        text.Append(terms);

        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 20);
        row.AddCssClass("card");
        art.Root.SetMarginStart(20);
        art.Root.SetMarginTop(20);
        art.Root.SetMarginBottom(20);
        text.SetMarginTop(20);
        text.SetMarginBottom(20);
        text.SetMarginEnd(20);
        row.Append(art.Root);
        row.Append(text);
        return row;
    }

    // Wraps the buttons under each other in a narrow Settings window instead of widening the card past it.
    private static Adw.WrapBox ButtonRow()
    {
        var row = Adw.WrapBox.New();
        row.SetChildSpacing(8);
        row.SetLineSpacing(8);
        return row;
    }

    private void RebuildPerks()
    {
        if (_included is not { } expander) return;
        foreach (var row in _perkRows) expander.Remove(row);
        _perkRows.Clear();
        foreach (var perk in ViewModel.OfferPerks.ToList())
        {
            var row = WinoSettingsRows.Action(perk.Title, perk.Description, perk.Icon);
            if (!string.IsNullOrWhiteSpace(perk.TrailingText))
            {
                var trailing = IntelligenceViews.Text(perk.TrailingText, "caption", "dim-label");
                trailing.SetXalign(1);
                trailing.SetWrapMode(Pango.WrapMode.Word);
                trailing.SetMaxWidthChars(14);
                WinoSettingsRows.Suffix(row, trailing);
            }
            expander.AddRow(row);
            _perkRows.Add(row);
        }
    }

    #endregion

    #region Subscribed

    private IEnumerable<WinoSettingsGroup> SubscribedGroups()
    {
        var vm = ViewModel;
        var main = Page.AddGroup();
        main.Add(StatusExpander());
        var assistant = Settings.Command(Translator.WinoIntelligence_Voice_Title, vm.AssistantHomeDescription, WinoIconGlyph.Microphone, vm.OpenVoiceControlCommand);
        Settings.Subtitle(assistant, vm, nameof(vm.AssistantHomeDescription), static s => s.AssistantHomeDescription);
        main.Add(Settings.Visible(assistant, vm, nameof(vm.HasIntelligenceAccess), static s => s.ShowVoiceCommandsCard));
        main.Add(Settings.Command(Translator.WinoIntelligence_MailClassification_Title, Translator.WinoIntelligence_MailClassification_HomeDescription,
            WinoIconGlyph.Tag, vm.OpenMailClassificationCommand));
        main.Add(LanguagesExpander());
        yield return main;

        yield return Page.AddGroup(Translator.WinoIntelligence_Privacy_Header, null, ProviderAccessRow());
    }

    /// <summary>The status strip: brand tile, title, renewal line and an Active or Limit reached pill; inside, the plan and the usage.</summary>
    private Adw.ExpanderRow StatusExpander()
    {
        var vm = ViewModel;
        var expander = _status = WinoSettingsRows.Expander(Translator.WinoIntelligence_SettingsTitle);
        expander.AddPrefix(IntelligenceViews.BrandTile(40, 20));
        Settings.Subtitle(expander, vm, nameof(vm.SubscriptionRenewalLine), static s => s.SubscriptionRenewalLine);
        var active = Settings.Visible(IntelligenceViews.Pill(Translator.WinoAccount_Management_ActiveShort, "success"), vm, nameof(vm.IsLimitReached), static s => s.IsWithinLimits);
        var limit = Settings.Visible(IntelligenceViews.Pill(Translator.WinoIntelligence_Status_LimitReached, "error"), vm, nameof(vm.IsLimitReached), static s => s.IsLimitReached);
        WinoSettingsRows.Suffix(expander, active);
        WinoSettingsRows.Suffix(expander, limit);

        var exhausted = WinoSettingsRows.Action(string.Empty);
        WinoSettingsRows.SetGlyph(exhausted, WinoIconGlyph.Warning);
        exhausted.AddCssClass("warning");
        Bind.BindAny(static s => s.HasExhaustedBuckets ? s.ExhaustedBucketsText : null, text =>
        {
            exhausted.SetTitle(text ?? string.Empty);
            exhausted.SetVisible(!string.IsNullOrWhiteSpace(text));
        });
        expander.AddRow(exhausted);

        var plan = WinoSettingsRows.Action(Translator.WinoAccount_Management_SubscriptionLabel, null, WinoIconGlyph.Payment);
        Settings.Subtitle(plan, vm, nameof(vm.AiPackRenewalOrCancellationText), static s => s.AiPackRenewalOrCancellationText);
        var manage = WinoA11y.Rename(Settings.Button(Translator.WinoIntelligence_ManageSubscription, vm.ManageSubscriptionCommand), Translator.WinoIntelligence_ManageSubscription);
        manage.SetValign(Gtk.Align.Center);
        Settings.Visible(manage, vm, nameof(vm.CanManageSubscription), static s => s.CanManageSubscription);
        var refresh = WinoA11y.Rename(Settings.Button(Translator.Buttons_Refresh, vm.RefreshPurchasesCommand), Translator.Buttons_Refresh);
        refresh.SetValign(Gtk.Align.Center);
        WinoSettingsRows.Suffix(plan, manage);
        WinoSettingsRows.Suffix(plan, refresh);
        expander.AddRow(plan);

        var unavailable = WinoSettingsRows.Action(Translator.WinoAccount_Management_IntelligenceUsageUnavailable);
        _usageUnavailable = Settings.Visible(unavailable, vm, nameof(vm.IsIntelligenceUsageAvailable), static s => !s.IsIntelligenceUsageAvailable);
        expander.AddRow(unavailable);
        Settings.Collection(vm.IntelligenceUsageItems, RebuildUsage);
        return expander;
    }

    private void RebuildUsage()
    {
        if (_status is not { } expander) return;
        foreach (var row in _usageRows) expander.Remove(row);
        _usageRows.Clear();
        if (_usageUnavailable is not null) expander.Remove(_usageUnavailable);
        foreach (var item in ViewModel.IntelligenceUsageItems.ToList())
        {
            var row = WinoSettingsRows.Action(item.DisplayName);
            var value = IntelligenceViews.Text(item.Value, "caption", "dim-label");
            value.SetXalign(1);
            var bar = IntelligenceViews.Bar(item.Percentage, item.IsExhausted, 180);
            WinoA11y.Label(bar, $"{item.DisplayName}: {item.Value}");
            var stack = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
            stack.SetValign(Gtk.Align.Center);
            stack.Append(value);
            stack.Append(bar);
            WinoSettingsRows.Suffix(row, stack);
            expander.AddRow(row);
            _usageRows.Add(row);
        }
        if (_usageUnavailable is not null) expander.AddRow(_usageUnavailable);
    }

    private Adw.ExpanderRow LanguagesExpander()
    {
        var vm = ViewModel;
        var translate = Settings.ChoiceSuffix<Wino.Mail.ViewModels.Common.WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm,
            Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Title,
            static s => s.AvailableAiLanguages, static o => o.Label,
            nameof(vm.SelectedDefaultTranslationLanguage), static s => s.SelectedDefaultTranslationLanguage, static (s, v) => s.SelectedDefaultTranslationLanguage = v);
        var summarize = Settings.ChoiceSuffix<Wino.Mail.ViewModels.Common.WinoAccountManagementPageViewModel, AiTranslateLanguageOption>(vm,
            Translator.SettingsAppPreferences_AiSummarizeLanguage_Title,
            static s => s.AvailableAiLanguages, static o => o.Label,
            nameof(vm.SelectedSummarizeLanguage), static s => s.SelectedSummarizeLanguage, static (s, v) => s.SelectedSummarizeLanguage = v);
        var translateRow = WinoSettingsRows.Action(Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Title, Translator.SettingsAppPreferences_AiDefaultTranslationLanguage_Description);
        WinoSettingsRows.Suffix(translateRow, translate);
        var summarizeRow = WinoSettingsRows.Action(Translator.SettingsAppPreferences_AiSummarizeLanguage_Title, Translator.SettingsAppPreferences_AiSummarizeLanguage_Description);
        WinoSettingsRows.Suffix(summarizeRow, summarize);
        return _languages = WinoSettingsRows.Expander(Translator.WinoIntelligence_Languages_Title, Translator.WinoIntelligence_Languages_Description, WinoIconGlyph.Translate,
            false, translateRow, summarizeRow);
    }

    /// <summary>AI provider access: the account-wide consent switch, the accepted date and the policy link.</summary>
    private Adw.ActionRow ProviderAccessRow()
    {
        var vm = ViewModel;
        var row = WinoSettingsRows.Action(Translator.WinoIntelligence_Privacy_Title, Translator.WinoIntelligence_ProviderAccess_Description, WinoIconGlyph.Shield);
        Settings.Bind(vm, nameof(vm.ConsentAcceptedText), static s => s.ConsentAcceptedText, text => row.SetSubtitle(string.IsNullOrWhiteSpace(text)
            ? Translator.WinoIntelligence_ProviderAccess_Description
            : $"{Translator.WinoIntelligence_ProviderAccess_Description}\n{text}"));
        var policy = IntelligenceViews.Link(Translator.WinoIntelligence_Policy_ReviewLink);
        var policyCommand = vm.OpenConsentPolicyCommand;
        Settings.Own(GtkSignals.OnClicked(policy, () =>
        {
            if (policyCommand.CanExecute(null)) policyCommand.Execute(null);
        }));
        var busy = Adw.Spinner.New();
        busy.SetValign(Gtk.Align.Center);
        var toggle = _consent = Gtk.Switch.New();
        toggle.SetValign(Gtk.Align.Center);
        WinoA11y.Label(toggle, Translator.WinoIntelligence_Privacy_Title);
        Settings.Bind(vm, nameof(vm.IsConsentBusy), static s => s.IsConsentBusy, on =>
        {
            busy.SetVisible(on);
            toggle.SetSensitive(!on);
        });
        Settings.Bind(vm, nameof(vm.IsConsentGranted), static s => s.IsConsentGranted, granted =>
        {
            if (_applyingConsent) return;
            _applyingConsent = true;
            try { toggle.SetActive(granted); }
            finally { _applyingConsent = false; }
        });
        Settings.Own(GtkSignals.OnNotify(toggle, "active", () => Observe(ConsentToggledAsync())));
        WinoSettingsRows.Suffix(row, policy);
        WinoSettingsRows.Suffix(row, busy);
        WinoSettingsRows.Suffix(row, toggle);
        return row;
    }

    /// <summary>Port of the macOS ConsentToggledAsync: on asks through the policy dialog, off confirms first; the switch then shows the ViewModel again.</summary>
    private async Task ConsentToggledAsync()
    {
        var vm = ViewModel;
        if (_consent is not { } toggle || _applyingConsent || vm.IsConsentBusy || toggle.GetActive() == vm.IsConsentGranted) return;
        _applyingConsent = true;
        try
        {
            if (toggle.GetActive())
            {
                if (toggle.GetRoot() is Gtk.Window window)
                    await IntelligenceConsentDialog.PresentAsync(window, vm.ConsentPolicyUri, _launcher, () => vm.SetIntelligenceConsentAsync(true));
            }
            else if (await _dialogs.ShowConfirmationDialogAsync(Translator.WinoAccount_IntelligenceConsentDisableMessage,
                         Translator.WinoAccount_IntelligenceConsentDisableTitle, Translator.WinoAccount_DisableConsent))
            {
                await vm.SetIntelligenceConsentAsync(false);
            }
        }
        catch (Exception exception) { ReportError(exception); }
        finally
        {
            _applyingConsent = false;
            if (!IsReleased) RunOnUI(() =>
            {
                _applyingConsent = true;
                try { toggle.SetActive(vm.IsConsentGranted); }
                finally { _applyingConsent = false; }
            });
        }
    }

    #endregion
}
