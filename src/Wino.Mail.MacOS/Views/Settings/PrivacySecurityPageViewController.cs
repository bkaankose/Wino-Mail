using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Privacy and security (Windows PrivacySecurityPage): the summary card, the three remote content modes, sender
/// exceptions, links, sender addresses, attachments, what Wino shares and the related settings.
/// </summary>
public sealed class PrivacySecurityPageViewController(PrivacySecurityPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<PrivacySecurityPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        Add(BuildHero());

        var junkInfo = InfoBar(WinoInfoBarSeverity.Informational, null, Translator.PrivacySecurity_JunkInfo);
        junkInfo.IsClosable = false;
        AddGroup(Translator.PrivacySecurity_RemoteContentHeader, BuildModePicker(), junkInfo);

        var rulesSummary = Row(
            Bind.Label(vm, nameof(vm.RuleCountText), s => s.RuleCountText, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText),
            Bind.Button(Translator.PrivacySecurity_SenderRules_Manage, vm.OpenSenderRulesCommand));
        AddGroup(Translator.PrivacySecurity_ExceptionsHeader,
            Expander(Translator.PrivacySecurity_SenderRules_Title, Translator.PrivacySecurity_SenderRules_Description, WinoIconGlyph.People, rulesSummary,
                Card(Translator.PrivacySecurity_Contacts_Title, Translator.PrivacySecurity_Contacts_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.LoadRemoteContentFromContacts), s => s.LoadRemoteContentFromContacts, (s, v) => s.LoadRemoteContentFromContacts = v, Translator.PrivacySecurity_Contacts_Title)),
                Card(Translator.PrivacySecurity_Recipients_Title, Translator.PrivacySecurity_Recipients_Description, WinoIconGlyph.None,
                    Bind.Switch(p, nameof(p.LoadRemoteContentFromRecipients), s => s.LoadRemoteContentFromRecipients, (s, v) => s.LoadRemoteContentFromRecipients = v, Translator.PrivacySecurity_Recipients_Title))),
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

        AddGroup(Translator.PrivacySecurity_SharingHeader,
            Bind.Enabled(Card(Translator.PrivacySecurity_Gravatar_Title, Translator.PrivacySecurity_Gravatar_Description, WinoIconGlyph.PersonSquare,
                    Bind.Switch(p, nameof(p.IsGravatarEnabled), s => s.IsGravatarEnabled, (s, v) => s.IsGravatarEnabled = v, Translator.PrivacySecurity_Gravatar_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Bind.Enabled(Card(Translator.PrivacySecurity_Favicons_Title, Translator.PrivacySecurity_Favicons_Description, WinoIconGlyph.Globe,
                    Bind.Switch(p, nameof(p.IsFaviconEnabled), s => s.IsFaviconEnabled, (s, v) => s.IsFaviconEnabled = v, Translator.PrivacySecurity_Favicons_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Card(Translator.PrivacySecurity_Logging_Title, Translator.PrivacySecurity_Logging_Description, WinoIconGlyph.Bug,
                Row(Bind.Button(Translator.PrivacySecurity_OpenLogFolder, vm.OpenLogFolderCommand),
                    Bind.Switch(p, nameof(p.IsLoggingEnabled), s => s.IsLoggingEnabled, (s, v) => s.IsLoggingEnabled = v, Translator.PrivacySecurity_Logging_Title))));

        WinoSettingsCard? notifications = null;
        if (vm.IsNotificationsLinkAvailable)
        {
            notifications = CommandCard(Translator.PrivacySecurity_RelatedNotifications_Title, Translator.PrivacySecurity_RelatedNotifications_Description, WinoIconGlyph.Alert, vm.OpenNotificationSettingsCommand);
            notifications.Content = Bind.Label(vm, nameof(vm.NotificationContentText), s => s.NotificationContentText, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText);
        }
        AddGroup(Translator.PrivacySecurity_RelatedHeader,
            notifications,
            vm.IsSignaturesLinkAvailable
                ? CommandCard(Translator.PrivacySecurity_RelatedSignatures_Title, Translator.PrivacySecurity_RelatedSignatures_Description, WinoIconGlyph.Certificate, vm.OpenSignatureAndEncryptionCommand)
                : null,
            vm.IsIntelligenceLinkAvailable
                ? CommandCard(Translator.PrivacySecurity_RelatedIntelligence_Title, Translator.PrivacySecurity_RelatedIntelligence_Description, WinoIconGlyph.Sparkle, vm.OpenWinoIntelligenceCommand)
                : null);
    }

    /// <summary>The summary card: the shield, the current protection and three status chips.</summary>
    private NSView BuildHero()
    {
        var vm = ViewModel;
        var icon = new NSImageView { Image = WinoIcons.Image(WinoIconGlyph.Shield, 40, WinoStyle.Accent), TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(icon, 40, 40);

        var title = Bind.Label(vm, nameof(vm.HeroTitle), s => s.HeroTitle, WinoStyle.Heading, WinoStyle.PrimaryText, 0);
        var description = Bind.Label(vm, nameof(vm.HeroDescription), s => s.HeroDescription, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);
        var chips = WinoLayout.HStack(8,
            Chip(nameof(vm.HeroModeStatus), s => s.HeroModeStatus),
            Chip(nameof(vm.HeroRuleStatus), s => s.HeroRuleStatus),
            Chip(nameof(vm.HeroLinkStatus), s => s.HeroLinkStatus));
        chips.SetClippingResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);

        var text = WinoLayout.VStack(6, title, description, chips);
        text.Alignment = NSLayoutAttribute.Leading;
        text.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        title.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
        description.WidthAnchor.ConstraintEqualTo(text.WidthAnchor).Active = true;
        chips.WidthAnchor.ConstraintLessThanOrEqualTo(text.WidthAnchor).Active = true;

        var row = WinoLayout.HStack(20, icon, text);
        row.Alignment = NSLayoutAttribute.Top;
        var hero = new WinoSurfaceView { Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1, CornerRadius = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(row, hero, 20, 20, 20, 20);
        return hero;
    }

    private NSView Chip(string property, Func<PrivacySecurityPageViewModel, string> read)
    {
        var label = Bind.Label(ViewModel, property, read, WinoStyle.Caption, WinoStyle.PrimaryText);
        var chip = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, Stroke = WinoSettingsStyle.CardStroke, StrokeWidth = 1, CornerRadius = 10, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, chip, 2, 8, 3, 8);
        chip.SetContentCompressionResistancePriority(300, NSLayoutConstraintOrientation.Horizontal);
        return chip;
    }

    /// <summary>The three mode cards side by side, each with a radio, its loads and blocks lists and a note.</summary>
    private NSView BuildModePicker()
    {
        var row = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Horizontal,
            Spacing = 8,
            Distribution = NSStackViewDistribution.FillEqually,
            Alignment = NSLayoutAttribute.Top,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(row, Translator.PrivacySecurity_RemoteContentHeader);
        NSView? first = null;
        foreach (var option in ViewModel.ModeOptions)
        {
            var card = ModeCard(option);
            row.AddArrangedSubview(card);
            if (first is null) first = card;
            else card.HeightAnchor.ConstraintEqualTo(first.HeightAnchor).Active = true;
        }
        return row;
    }

    private NSView ModeCard(RemoteContentModeOption option)
    {
        var radio = new NSButton { Title = string.Empty, TranslatesAutoresizingMaskIntoConstraints = false };
        radio.SetButtonType(NSButtonType.Radio);
        WinoAccessibility.Label(radio, option.Title);
        Bind.OnActivated(radio, () => option.IsSelected = true);

        var title = WinoStyle.Label(option.Title, WinoSettingsStyle.CardTitle, WinoStyle.PrimaryText, 0);
        var heading = WinoLayout.HStack(6, radio, title);
        heading.Alignment = NSLayoutAttribute.FirstBaseline;
        if (option.IsRecommended) heading.AddArrangedSubview(RecommendedTag(option.RecommendedText));

        var content = WinoLayout.VStack(6);
        content.Alignment = NSLayoutAttribute.Leading;
        void Append(NSView view, double spacingBefore = 0)
        {
            content.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(content.WidthAnchor).Active = true;
            if (spacingBefore > 0 && content.ArrangedSubviews.Length > 1)
                content.SetCustomSpacing((nfloat)(6 + spacingBefore), content.ArrangedSubviews[^2]);
        }

        heading.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        Append(heading);
        Append(WinoStyle.Label(option.Tagline, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0));
        Append(WinoStyle.Label(option.LoadsHeader, WinoStyle.CaptionStrong, WinoStyle.PrimaryText), 4);
        foreach (var line in option.Loads) Append(ListLine(WinoIconGlyph.Checkmark, WinoStyle.Success, line));
        Append(WinoStyle.Label(option.BlocksHeader, WinoStyle.CaptionStrong, WinoStyle.PrimaryText), 4);
        foreach (var line in option.Blocks) Append(ListLine(WinoIconGlyph.Dismiss, WinoStyle.Critical, line));
        Append(WinoStyle.Label(option.Note, WinoStyle.Caption, WinoStyle.SecondaryText, 0), 4);

        var card = new ModeCardView { Fill = WinoSettingsStyle.CardFill, CornerRadius = 8, TranslatesAutoresizingMaskIntoConstraints = false };
        card.AddSubview(content);
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        var minimal = card.HeightAnchor.ConstraintEqualTo(0);
        minimal.Priority = 1;
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(card.TopAnchor, 14),
            content.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 14),
            content.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -14),
            content.BottomAnchor.ConstraintLessThanOrEqualTo(card.BottomAnchor, -14),
            minimal
        ]);
        card.Clicked = () => option.IsSelected = true;

        Bind.Bind(option, nameof(option.IsSelected), o => o.IsSelected, selected =>
        {
            radio.State = selected ? NSCellStateValue.On : NSCellStateValue.Off;
            card.Stroke = selected ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
            card.StrokeWidth = selected ? 2 : 1;
        });
        return card;
    }

    private static NSView RecommendedTag(string text)
    {
        var label = WinoStyle.Label(text, WinoStyle.CaptionStrong, NSColor.White);
        var tag = new WinoSurfaceView { Fill = WinoStyle.Accent, CornerRadius = 9, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(label, tag, 1, 7, 2, 7);
        tag.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        tag.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        return tag;
    }

    private static NSView ListLine(WinoIconGlyph glyph, NSColor tint, string text)
    {
        var icon = new NSImageView { Image = WinoIcons.Image(glyph, 11, tint), TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(icon, 12, 12);
        var label = WinoStyle.Label(text, WinoSettingsStyle.CardDescription, WinoStyle.PrimaryText, 0);
        var line = WinoLayout.HStack(6, icon, label);
        line.Alignment = NSLayoutAttribute.Top;
        line.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        return line;
    }

    /// <summary>A mode card: a click anywhere on it selects the mode.</summary>
    private sealed class ModeCardView : WinoSurfaceView
    {
        public Action? Clicked { get; set; }

        public override void MouseDown(NSEvent theEvent)
        {
        }

        public override void MouseUp(NSEvent theEvent)
        {
            if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Clicked?.Invoke();
        }
    }
}
