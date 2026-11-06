using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Privacy and security (Windows PrivacySecurityPage, Mac PrivacySecurityPageViewController): the summary row, the
/// three remote content modes as a checked list, sender exceptions, links, sender addresses, attachments, what Wino
/// shares and the related settings.
/// </summary>
public sealed class PrivacySecurityPageViewController(PrivacySecurityPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<PrivacySecurityPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        AddGroup(null, BuildHero());

        var modes = Group(Translator.PrivacySecurity_RemoteContentHeader);
        foreach (var option in vm.ModeOptions) modes.Add(ModeRow(option));
        // The Windows informational bar under the modes is the group footer, as in iOS Settings.
        modes.Footer = Translator.PrivacySecurity_JunkInfo;
        Add(modes);

        AddGroup(Translator.PrivacySecurity_ExceptionsHeader,
            CommandCard(Translator.PrivacySecurity_SenderRules_Title, Translator.PrivacySecurity_SenderRules_Description, WinoIconGlyph.People, vm.OpenSenderRulesCommand)
                .WithContent(Bind.Label(vm, nameof(vm.RuleCountText), s => s.RuleCountText, WinoSettingsStyle.RowValue, WinoStyle.SecondaryText)),
            Card(Translator.PrivacySecurity_Contacts_Title, Translator.PrivacySecurity_Contacts_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.LoadRemoteContentFromContacts), s => s.LoadRemoteContentFromContacts, (s, v) => s.LoadRemoteContentFromContacts = v, Translator.PrivacySecurity_Contacts_Title)),
            Card(Translator.PrivacySecurity_Recipients_Title, Translator.PrivacySecurity_Recipients_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.LoadRemoteContentFromRecipients), s => s.LoadRemoteContentFromRecipients, (s, v) => s.LoadRemoteContentFromRecipients = v, Translator.PrivacySecurity_Recipients_Title)),
            Card(Translator.PrivacySecurity_Junk_Title, Translator.PrivacySecurity_Junk_Description, WinoIconGlyph.SpecialFolderJunk,
                Caption(Translator.PrivacySecurity_Junk_AlwaysBlocked)));

        AddGroup(Translator.PrivacySecurity_LinksHeader,
            Card(Translator.PrivacySecurity_StripLinks_Title, Translator.PrivacySecurity_StripLinks_Description, WinoIconGlyph.LinkDismiss,
                Bind.Switch(p, nameof(p.StripLinkTrackingParameters), s => s.StripLinkTrackingParameters, (s, v) => s.StripLinkTrackingParameters = v, Translator.PrivacySecurity_StripLinks_Title)),
            Card(Translator.PrivacySecurity_ConfirmLinks_Title, Translator.PrivacySecurity_ConfirmLinks_Description, WinoIconGlyph.Link,
                Bind.Switch(p, nameof(p.ConfirmBeforeOpeningLinks), s => s.ConfirmBeforeOpeningLinks, (s, v) => s.ConfirmBeforeOpeningLinks = v, Translator.PrivacySecurity_ConfirmLinks_Title)));

        AddGroup(Translator.PrivacySecurity_SendersHeader,
            Card(Translator.PrivacySecurity_ShowSenderAddress_Title, Translator.PrivacySecurity_ShowSenderAddress_Description, WinoIconGlyph.Person,
                Bind.Switch(p, nameof(p.AlwaysShowSenderAddress), s => s.AlwaysShowSenderAddress, (s, v) => s.AlwaysShowSenderAddress = v, Translator.PrivacySecurity_ShowSenderAddress_Title)));

        AddGroup(Translator.PrivacySecurity_AttachmentsHeader,
            vm.IsAttachmentSecurityCheckAvailable
                ? Card(Translator.PrivacySecurity_PlatformSecurity_Title, Translator.PrivacySecurity_PlatformSecurity_Description, WinoIconGlyph.Shield,
                    Bind.Switch(p, nameof(p.CheckAttachmentsWithPlatformSecurity), s => s.CheckAttachmentsWithPlatformSecurity, (s, v) => s.CheckAttachmentsWithPlatformSecurity = v, Translator.PrivacySecurity_PlatformSecurity_Title))
                : null,
            Card(Translator.PrivacySecurity_ExecutableWarning_Title, Translator.PrivacySecurity_ExecutableWarning_Description, WinoIconGlyph.Attachment,
                Bind.Switch(p, nameof(p.WarnBeforeOpeningExecutableAttachments), s => s.WarnBeforeOpeningExecutableAttachments, (s, v) => s.WarnBeforeOpeningExecutableAttachments = v, Translator.PrivacySecurity_ExecutableWarning_Title)));

        // iOS has no file manager to open a folder in; the log folder action exports the log (the ViewModel's fallback).
        AddGroup(Translator.PrivacySecurity_SharingHeader,
            Bind.Enabled(Card(Translator.PrivacySecurity_Gravatar_Title, Translator.PrivacySecurity_Gravatar_Description, WinoIconGlyph.PersonSquare,
                    Bind.Switch(p, nameof(p.IsGravatarEnabled), s => s.IsGravatarEnabled, (s, v) => s.IsGravatarEnabled = v, Translator.PrivacySecurity_Gravatar_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Bind.Enabled(Card(Translator.PrivacySecurity_Favicons_Title, Translator.PrivacySecurity_Favicons_Description, WinoIconGlyph.Globe,
                    Bind.Switch(p, nameof(p.IsFaviconEnabled), s => s.IsFaviconEnabled, (s, v) => s.IsFaviconEnabled = v, Translator.PrivacySecurity_Favicons_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Card(Translator.PrivacySecurity_Logging_Title, Translator.PrivacySecurity_Logging_Description, WinoIconGlyph.Bug,
                Bind.Switch(p, nameof(p.IsLoggingEnabled), s => s.IsLoggingEnabled, (s, v) => s.IsLoggingEnabled = v, Translator.PrivacySecurity_Logging_Title)),
            ActionRow(Bind.Button(Translator.PrivacySecurity_OpenLogFolder, vm.OpenLogFolderCommand)));

        AddGroup(Translator.PrivacySecurity_RelatedHeader,
            vm.IsNotificationsLinkAvailable
                ? CommandCard(Translator.PrivacySecurity_RelatedNotifications_Title, Translator.PrivacySecurity_RelatedNotifications_Description, WinoIconGlyph.Alert, vm.OpenNotificationSettingsCommand)
                    .WithContent(Bind.Label(vm, nameof(vm.NotificationContentText), s => s.NotificationContentText, WinoSettingsStyle.RowValue, WinoStyle.SecondaryText))
                : null,
            vm.IsSignaturesLinkAvailable
                ? CommandCard(Translator.PrivacySecurity_RelatedSignatures_Title, Translator.PrivacySecurity_RelatedSignatures_Description, WinoIconGlyph.Certificate, vm.OpenSignatureAndEncryptionCommand)
                : null,
            vm.IsIntelligenceLinkAvailable
                ? CommandCard(Translator.PrivacySecurity_RelatedIntelligence_Title, Translator.PrivacySecurity_RelatedIntelligence_Description, WinoIconGlyph.Sparkle, vm.OpenWinoIntelligenceCommand)
                : null);
    }

    /// <summary>The summary row: the current protection with its description and the three status lines.</summary>
    private WinoSettingsCard BuildHero()
    {
        var vm = ViewModel;
        var hero = Card(vm.HeroTitle, vm.HeroDescription, WinoIconGlyph.Shield);
        Bind.Bind(vm, nameof(vm.HeroTitle), s => s.HeroTitle, text => hero.Header = text ?? string.Empty);
        Bind.Bind(vm, nameof(vm.HeroDescription), s => s.HeroDescription, text => hero.Description = text);
        var chips = WinoLayout.VStack(6,
            Chip(nameof(vm.HeroModeStatus), s => s.HeroModeStatus),
            Chip(nameof(vm.HeroRuleStatus), s => s.HeroRuleStatus),
            Chip(nameof(vm.HeroLinkStatus), s => s.HeroLinkStatus));
        chips.Alignment = UIStackViewAlignment.Leading;
        hero.BottomContent = chips;
        hero.AccessibilityTraits |= UIAccessibilityTrait.Header;
        return hero;
    }

    private UIView Chip(string property, Func<PrivacySecurityPageViewModel, string> read)
    {
        var label = Bind.Label(ViewModel, property, read, WinoStyle.Caption, WinoStyle.PrimaryText, 0);
        var chip = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, CornerRadius = 10, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, chip, 3, 10, 4, 10);
        return chip;
    }

    /// <summary>One mode: title and tagline, a checkmark when selected, and its loads, blocks and note underneath.</summary>
    private WinoSettingsCard ModeRow(RemoteContentModeOption option)
    {
        var check = new UIImageView { Image = WinoIcons.Image(WinoIconGlyph.Checkmark, 18, WinoStyle.Accent), TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(check, 22, 22);
        var trailing = option.IsRecommended ? Row(SettingsRowParts.Tag(option.RecommendedText), check) : Row(check);

        var row = NavigationCard(option.Title, option.Tagline, WinoIconGlyph.None, () => option.IsSelected = true, trailing);
        row.ShowsChevron = false;

        var details = WinoLayout.VStack(4);
        details.Alignment = UIStackViewAlignment.Fill;
        details.AddArrangedSubview(WinoStyle.Label(option.LoadsHeader, WinoStyle.Scaled(12, UIFontWeight.Semibold), WinoStyle.PrimaryText));
        foreach (var line in option.Loads) details.AddArrangedSubview(ListLine(WinoIconGlyph.Checkmark, WinoStyle.Success, line));
        details.AddArrangedSubview(WinoStyle.Label(option.BlocksHeader, WinoStyle.Scaled(12, UIFontWeight.Semibold), WinoStyle.PrimaryText));
        foreach (var line in option.Blocks) details.AddArrangedSubview(ListLine(WinoIconGlyph.Dismiss, WinoStyle.Critical, line));
        details.AddArrangedSubview(WinoStyle.Label(option.Note, WinoStyle.Caption, WinoStyle.SecondaryText, 0));
        details.SetCustomSpacing(8, details.ArrangedSubviews[option.Loads.Length]);
        row.BottomContent = details;

        Bind.Bind(option, nameof(option.IsSelected), o => o.IsSelected, selected =>
        {
            check.Hidden = !selected;
            row.AccessibilityTraits = selected ? row.AccessibilityTraits | UIAccessibilityTrait.Selected : row.AccessibilityTraits & ~UIAccessibilityTrait.Selected;
        });
        return row;
    }

    private static UIView ListLine(WinoIconGlyph glyph, UIColor tint, string text)
    {
        var icon = new UIImageView { Image = WinoIcons.Image(glyph, 12, tint), TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(icon, 14, 14);
        var label = WinoStyle.Label(text, WinoSettingsStyle.CardDescription, WinoStyle.PrimaryText, 0);
        var line = WinoLayout.HStack(6, icon, label);
        line.Alignment = UIStackViewAlignment.Top;
        return line;
    }
}

internal static class PrivacySettingsCardExtensions
{
    /// <summary>Sets a command card's trailing value (the Windows card's secondary text).</summary>
    public static WinoSettingsCard WithContent(this WinoSettingsCard card, UIView content)
    {
        card.Content = content;
        return card;
    }
}
