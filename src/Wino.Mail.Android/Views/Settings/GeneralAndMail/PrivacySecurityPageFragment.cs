using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// Privacy and security (Windows PrivacySecurityPage, same sections and order). The Windows mode cards become one picker
/// row whose detail page lists the three modes, with the selected mode's tagline and note as its footer. The sender
/// rules expander is flattened into a row that opens the rules page and the two exception switches. The Windows
/// Security card follows the attachment security capability, which is off on Android.
/// </summary>
public sealed class PrivacySecurityPageFragment : SettingsPageFragment<PrivacySecurityPageViewModel>
{
    protected override void BuildPage()
    {
        var vm = VM;
        var p = vm.PreferencesService;

        AddHero(vm);

        var mode = StackedCard(Translator.PrivacySecurity_RemoteContentHeader, ModeDescription(vm), WinoIconGlyph.Shield,
            Bind.PopUp<PrivacySecurityPageViewModel, RemoteContentModeOption>(vm, s => s.ModeOptions, option => option.Title,
                nameof(vm.SelectedMode), s => Array.Find(s.ModeOptions, option => option.Mode == s.SelectedMode), (s, option) => s.SelectedMode = option.Mode));
        Bind.Bind(vm, nameof(vm.SelectedMode), s => s.SelectedMode, _ => mode.Description = ModeDescription(vm));
        var remoteContent = Group(Translator.PrivacySecurity_RemoteContentHeader, mode);
        remoteContent.Footer = Translator.PrivacySecurity_JunkInfo;
        Add(remoteContent);

        AddGroup(Translator.PrivacySecurity_ExceptionsHeader,
            NavigationCard(Translator.PrivacySecurity_SenderRules_Title, Translator.PrivacySecurity_SenderRules_Description, WinoIconGlyph.People,
                () => vm.OpenSenderRulesCommand.Execute(null),
                Bind.Label(vm, nameof(vm.RuleCountText), s => s.RuleCountText)),
            Card(Translator.PrivacySecurity_Contacts_Title, Translator.PrivacySecurity_Contacts_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.LoadRemoteContentFromContacts), s => s.LoadRemoteContentFromContacts, (s, v) => s.LoadRemoteContentFromContacts = v, Translator.PrivacySecurity_Contacts_Title)),
            Card(Translator.PrivacySecurity_Recipients_Title, Translator.PrivacySecurity_Recipients_Description, WinoIconGlyph.None,
                Bind.Switch(p, nameof(p.LoadRemoteContentFromRecipients), s => s.LoadRemoteContentFromRecipients, (s, v) => s.LoadRemoteContentFromRecipients = v, Translator.PrivacySecurity_Recipients_Title)),
            Card(Translator.PrivacySecurity_Junk_Title, Translator.PrivacySecurity_Junk_Description, WinoIconGlyph.None,
                Caption(Translator.PrivacySecurity_Junk_AlwaysBlocked)));

        AddGroup(Translator.PrivacySecurity_LinksHeader,
            Card(Translator.PrivacySecurity_StripLinks_Title, Translator.PrivacySecurity_StripLinks_Description, WinoIconGlyph.Link,
                Bind.Switch(p, nameof(p.StripLinkTrackingParameters), s => s.StripLinkTrackingParameters, (s, v) => s.StripLinkTrackingParameters = v, Translator.PrivacySecurity_StripLinks_Title)),
            Card(Translator.PrivacySecurity_ConfirmLinks_Title, Translator.PrivacySecurity_ConfirmLinks_Description, WinoIconGlyph.Open,
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
            Bind.Enabled(Card(Translator.PrivacySecurity_Gravatar_Title, Translator.PrivacySecurity_Gravatar_Description, WinoIconGlyph.Person,
                    Bind.Switch(p, nameof(p.IsGravatarEnabled), s => s.IsGravatarEnabled, (s, v) => s.IsGravatarEnabled = v, Translator.PrivacySecurity_Gravatar_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Bind.Enabled(Card(Translator.PrivacySecurity_Favicons_Title, Translator.PrivacySecurity_Favicons_Description, WinoIconGlyph.Globe,
                    Bind.Switch(p, nameof(p.IsFaviconEnabled), s => s.IsFaviconEnabled, (s, v) => s.IsFaviconEnabled = v, Translator.PrivacySecurity_Favicons_Title)),
                p, nameof(p.IsShowSenderPicturesEnabled), s => s.IsShowSenderPicturesEnabled),
            Card(Translator.PrivacySecurity_Logging_Title, Translator.PrivacySecurity_Logging_Description, WinoIconGlyph.Bug,
                Bind.Switch(p, nameof(p.IsLoggingEnabled), s => s.IsLoggingEnabled, (s, v) => s.IsLoggingEnabled = v, Translator.PrivacySecurity_Logging_Title)),
            ActionCard(Translator.PrivacySecurity_OpenLogFolder, vm.OpenLogFolderCommand));

        AddGroup(Translator.PrivacySecurity_RelatedHeader,
            vm.IsNotificationsLinkAvailable
                ? NavigationCard(Translator.PrivacySecurity_RelatedNotifications_Title, Translator.PrivacySecurity_RelatedNotifications_Description, WinoIconGlyph.Alert,
                    () => vm.OpenNotificationSettingsCommand.Execute(null),
                    Bind.Label(vm, nameof(vm.NotificationContentText), s => s.NotificationContentText))
                : null,
            vm.IsSignaturesLinkAvailable
                ? NavigationCard(Translator.PrivacySecurity_RelatedSignatures_Title, Translator.PrivacySecurity_RelatedSignatures_Description, WinoIconGlyph.None,
                    () => vm.OpenSignatureAndEncryptionCommand.Execute(null))
                : null,
            vm.IsIntelligenceLinkAvailable
                ? NavigationCard(Translator.PrivacySecurity_RelatedIntelligence_Title, Translator.PrivacySecurity_RelatedIntelligence_Description, WinoIconGlyph.None,
                    () => vm.OpenWinoIntelligenceCommand.Execute(null))
                : null);
    }

    /// <summary>The Windows summary at the top: what the current mode means for the reader, without the status chips.</summary>
    private void AddHero(PrivacySecurityPageViewModel vm)
    {
        var title = Text(vm.HeroTitle, WinoTextStyle.Subtitle);
        AndroidX.Core.View.ViewCompat.SetAccessibilityHeading(title, true);
        var description = Text(vm.HeroDescription, WinoTextStyle.Description);
        Bind.Bind(vm, nameof(vm.HeroTitle), s => s.HeroTitle, value => title.Text = value);
        Bind.Bind(vm, nameof(vm.HeroDescription), s => s.HeroDescription, value => description.Text = value);
        var hero = new FrameLayout(Ctx);
        hero.AddView(Stack(Resource.Dimension.wino_space_3xs, title, description),
            new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        Add(hero);
    }

    private static string ModeDescription(PrivacySecurityPageViewModel vm)
        => Array.Find(vm.ModeOptions, option => option.Mode == vm.SelectedMode) is { } selected
            ? $"{selected.Tagline} {selected.Note}"
            : string.Empty;
}
