using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Privacy and security (Windows PrivacySecurityPage): the summary card, the three remote content modes, sender
/// exceptions, links and the link browser, sender addresses, attachments and the related settings.
/// </summary>
internal sealed class PrivacySecurityPage(PrivacySecurityPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<PrivacySecurityPageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        AddGroup(null, Hero());

        Page.AddGroup(Translator.PrivacySecurity_RemoteContentHeader, null,
            ModePicker(),
            WinoSettingsRows.Custom(WinoSettingsRows.Caption(Translator.PrivacySecurity_JunkInfo)));

        var rules = WinoSettingsRows.Expander(Translator.PrivacySecurity_SenderRules_Title, Translator.PrivacySecurity_SenderRules_Description, WinoIconGlyph.People, false,
            Settings.Switch(p, Translator.PrivacySecurity_Contacts_Title, Translator.PrivacySecurity_Contacts_Description, WinoIconGlyph.None,
                nameof(p.LoadRemoteContentFromContacts), static s => s.LoadRemoteContentFromContacts, static (s, v) => s.LoadRemoteContentFromContacts = v),
            Settings.Switch(p, Translator.PrivacySecurity_Recipients_Title, Translator.PrivacySecurity_Recipients_Description, WinoIconGlyph.None,
                nameof(p.LoadRemoteContentFromRecipients), static s => s.LoadRemoteContentFromRecipients, static (s, v) => s.LoadRemoteContentFromRecipients = v));
        WinoSettingsRows.Suffix(rules, Settings.LabelSuffix(vm, nameof(vm.RuleCountText), static s => s.RuleCountText));
        WinoSettingsRows.Suffix(rules, Settings.Button(Translator.PrivacySecurity_SenderRules_Manage, vm.OpenSenderRulesCommand));
        Page.AddGroup(Translator.PrivacySecurity_ExceptionsHeader, null, rules);

        Page.AddGroup(Translator.PrivacySecurity_LinksHeader, null,
            Settings.Switch(p, Translator.PrivacySecurity_StripLinks_Title, Translator.PrivacySecurity_StripLinks_Description, WinoIconGlyph.LinkDismiss,
                nameof(p.StripLinkTrackingParameters), static s => s.StripLinkTrackingParameters, static (s, v) => s.StripLinkTrackingParameters = v),
            Settings.Switch(p, Translator.PrivacySecurity_ConfirmLinks_Title, Translator.PrivacySecurity_ConfirmLinks_Description, WinoIconGlyph.Link,
                nameof(p.ConfirmBeforeOpeningLinks), static s => s.ConfirmBeforeOpeningLinks, static (s, v) => s.ConfirmBeforeOpeningLinks = v),
            vm.IsLinkBrowserSupported ? LinkBrowser(vm) : null);

        Page.AddGroup(Translator.PrivacySecurity_SendersHeader, null,
            Settings.Switch(p, Translator.PrivacySecurity_ShowSenderAddress_Title, Translator.PrivacySecurity_ShowSenderAddress_Description, WinoIconGlyph.Person,
                nameof(p.AlwaysShowSenderAddress), static s => s.AlwaysShowSenderAddress, static (s, v) => s.AlwaysShowSenderAddress = v));

        Page.AddGroup(Translator.PrivacySecurity_AttachmentsHeader, null,
            vm.IsAttachmentSecurityCheckAvailable
                ? Settings.Switch(p, Translator.PrivacySecurity_PlatformSecurity_Title, Translator.PrivacySecurity_PlatformSecurity_Description, WinoIconGlyph.Shield,
                    nameof(p.CheckAttachmentsWithPlatformSecurity), static s => s.CheckAttachmentsWithPlatformSecurity, static (s, v) => s.CheckAttachmentsWithPlatformSecurity = v)
                : null,
            Settings.Switch(p, Translator.PrivacySecurity_ExecutableWarning_Title, Translator.PrivacySecurity_ExecutableWarning_Description, WinoIconGlyph.Attachment,
                nameof(p.WarnBeforeOpeningExecutableAttachments), static s => s.WarnBeforeOpeningExecutableAttachments, static (s, v) => s.WarnBeforeOpeningExecutableAttachments = v));

        Adw.ActionRow? notifications = null;
        if (vm.IsNotificationsLinkAvailable)
        {
            notifications = Settings.Command(Translator.PrivacySecurity_RelatedNotifications_Title, Translator.PrivacySecurity_RelatedNotifications_Description, WinoIconGlyph.Alert,
                vm.OpenNotificationSettingsCommand, detail: Settings.LabelSuffix(vm, nameof(vm.NotificationContentText), static s => s.NotificationContentText));
        }
        Page.AddGroup(Translator.PrivacySecurity_RelatedHeader, null,
            notifications,
            vm.IsSignaturesLinkAvailable
                ? Settings.Command(Translator.PrivacySecurity_RelatedSignatures_Title, Translator.PrivacySecurity_RelatedSignatures_Description, WinoIconGlyph.Certificate, vm.OpenSignatureAndEncryptionCommand)
                : null,
            vm.IsIntelligenceLinkAvailable
                ? Settings.Command(Translator.PrivacySecurity_RelatedIntelligence_Title, Translator.PrivacySecurity_RelatedIntelligence_Description, WinoIconGlyph.Sparkle, vm.OpenWinoIntelligencePrivacyCommand)
                : null);
    }

    private Adw.PreferencesRow Hero()
    {
        var vm = ViewModel;
        var shield = new WinoIcon(WinoIconGlyph.Shield, 40).Root;
        shield.AddCssClass("accent");
        shield.SetValign(Gtk.Align.Start);

        var title = Label("title-3", wrap: true);
        Settings.Bind(vm, nameof(vm.HeroTitle), static s => s.HeroTitle, text => title.SetText(text ?? string.Empty));
        var description = Label("dim-label", wrap: true);
        Settings.Bind(vm, nameof(vm.HeroDescription), static s => s.HeroDescription, text => description.SetText(text ?? string.Empty));

        var chips = Adw.WrapBox.New();
        chips.SetChildSpacing(8);
        chips.SetLineSpacing(6);
        chips.Append(Chip(nameof(vm.HeroModeStatus), static s => s.HeroModeStatus));
        chips.Append(Chip(nameof(vm.HeroRuleStatus), static s => s.HeroRuleStatus));
        chips.Append(Chip(nameof(vm.HeroLinkStatus), static s => s.HeroLinkStatus));

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        text.SetHexpand(true);
        text.Append(title);
        text.Append(description);
        text.Append(chips);

        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 20);
        row.SetMarginTop(8);
        row.SetMarginBottom(8);
        row.Append(shield);
        row.Append(text);
        return WinoSettingsRows.Custom(row);
    }

    private Gtk.Label Chip(string property, Func<PrivacySecurityPageViewModel, string?> read)
    {
        var chip = Label(WinoCssClass.Chip, wrap: false);
        chip.AddCssClass("caption");
        Settings.Bind(ViewModel, property, read, text => chip.SetText(text ?? string.Empty));
        return chip;
    }

    /// <summary>The three modes as one radio group of toggle buttons (AT-SPI can press them; it cannot toggle a check button).</summary>
    private Adw.PreferencesRow ModePicker()
    {
        var box = Adw.WrapBox.New();
        box.SetChildSpacing(8);
        box.SetLineSpacing(8);
        Gtk.ToggleButton? first = null;
        foreach (var option in ViewModel.ModeOptions)
        {
            var card = ModeCard(option);
            if (first is null) first = card;
            else card.SetGroup(first);
            box.Append(card);
        }
        return WinoSettingsRows.Custom(box, Translator.PrivacySecurity_RemoteContentHeader);
    }

    private Gtk.ToggleButton ModeCard(RemoteContentModeOption option)
    {
        var content = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        content.SetMarginTop(8);
        content.SetMarginBottom(8);
        content.SetMarginStart(6);
        content.SetMarginEnd(6);
        content.Append(Text(option.Title, "heading"));
        if (option.IsRecommended)
        {
            var tag = Text(option.RecommendedText, "caption-heading");
            tag.AddCssClass("accent");
            content.Append(tag);
        }
        content.Append(Text(option.Tagline, "dim-label"));
        content.Append(Text(option.LoadsHeader, "caption-heading"));
        foreach (var line in option.Loads) content.Append(Line(WinoIconGlyph.Checkmark, "success", line));
        content.Append(Text(option.BlocksHeader, "caption-heading"));
        foreach (var line in option.Blocks) content.Append(Line(WinoIconGlyph.Dismiss, "error", line));
        content.Append(Text(option.Note, "caption"));

        var card = Gtk.ToggleButton.New();
        card.AddCssClass("card");
        card.AddCssClass("flat");
        card.SetSizeRequest(220, -1);
        card.SetValign(Gtk.Align.Start);
        card.SetChild(content);
        WinoA11y.Label(card, option.Title);
        Settings.Bind(option, nameof(option.IsSelected), static o => o.IsSelected, card.SetActive);
        Settings.Own(GtkSignals.OnToggled(card, () =>
        {
            if (card.GetActive()) option.IsSelected = true;
        }));
        return card;
    }

    private static Gtk.Box Line(WinoIconGlyph glyph, string tint, string text)
    {
        var icon = new WinoIcon(glyph, 12).Root;
        icon.AddCssClass(tint);
        icon.SetValign(Gtk.Align.Start);
        var line = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        line.Append(icon);
        line.Append(Text(text, null));
        return line;
    }

    /// <summary>Which browser opens links, for all accounts or one account (Windows link browser expander).</summary>
    private Adw.ExpanderRow LinkBrowser(PrivacySecurityPageViewModel vm)
    {
        var note = WinoSettingsRows.Caption(null);
        Settings.Bind(vm, nameof(vm.CustomisedLinkBrowserAccountsText), static s => s.CustomisedLinkBrowserAccountsText, text => note.SetText(text ?? string.Empty));
        var noteRow = Settings.Visible(WinoSettingsRows.Custom(note), vm, nameof(vm.HasCustomisedLinkBrowserAccounts), static s => s.HasCustomisedLinkBrowserAccounts);

        var scope = WinoSettingsRows.Action(Translator.SettingsActionScope_Title);
        Settings.Subtitle(scope, vm, nameof(vm.LinkBrowserScopeDescription), static s => s.LinkBrowserScopeDescription);
        WinoSettingsRows.Suffix(scope, Settings.Visible(Settings.Button(Translator.SettingsLinkBrowser_UseAllAccounts, vm.UseAllAccountsLinkBrowserCommand),
            vm, nameof(vm.CanUseAllAccountsLinkBrowser), static s => s.CanUseAllAccountsLinkBrowser));
        WinoSettingsRows.Suffix(scope, Settings.ChoiceSuffix<PrivacySecurityPageViewModel, string>(vm, Translator.SettingsActionScope_Title,
            static s => s.LinkBrowserScopeOptions, static option => option,
            nameof(vm.SelectedLinkBrowserScopeIndex), static s => At(s.LinkBrowserScopeOptions, s.SelectedLinkBrowserScopeIndex),
            static (s, option) =>
            {
                var index = s.LinkBrowserScopeOptions.IndexOf(option);
                if (index >= 0) s.SelectedLinkBrowserScopeIndex = index;
            },
            itemsProperty: nameof(vm.LinkBrowserScopeOptions)));
        Settings.Visible(scope, vm, nameof(vm.IsLinkBrowserScopeAvailable), static s => s.IsLinkBrowserScopeAvailable);

        var browser = Settings.Choice<PrivacySecurityPageViewModel, string>(vm, Translator.SettingsLinkBrowser_Browser, null, WinoIconGlyph.None,
            static s => s.LinkBrowserOptions, static option => option,
            nameof(vm.SelectedLinkBrowserIndex), static s => At(s.LinkBrowserOptions, s.SelectedLinkBrowserIndex),
            static (s, option) =>
            {
                var index = s.LinkBrowserOptions.IndexOf(option);
                if (index >= 0) s.SelectedLinkBrowserIndex = index;
            },
            itemsProperty: nameof(vm.LinkBrowserOptions));
        Settings.Subtitle(browser, vm, nameof(vm.LinkBrowserDescription), static s => s.LinkBrowserDescription);

        return WinoSettingsRows.Expander(Translator.SettingsLinkBrowser_Title, Translator.SettingsLinkBrowser_Description, WinoIconGlyph.Globe, false,
            noteRow, scope, browser);
    }

    private static string? At(List<string> options, int index) => index >= 0 && index < options.Count ? options[index] : null;

    // A wrapping label asks for its whole text on one line as natural width; the cap lets the mode cards share a line.
    private static Gtk.Label Text(string? text, string? cssClass)
    {
        var label = Label(cssClass, wrap: true);
        label.SetText(text ?? string.Empty);
        label.SetMaxWidthChars(24);
        return label;
    }

    private static Gtk.Label Label(string? cssClass, bool wrap)
    {
        var label = Gtk.Label.New(string.Empty);
        label.SetXalign(0);
        if (wrap)
        {
            label.SetWrap(true);
            label.SetWrapMode(Pango.WrapMode.WordChar);
        }
        if (cssClass is not null) label.AddCssClass(cssClass);
        return label;
    }
}
