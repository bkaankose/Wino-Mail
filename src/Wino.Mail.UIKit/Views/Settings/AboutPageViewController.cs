using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>About: app identity, links, diagnostics and version (Windows AboutPage, same card order).</summary>
public sealed class AboutPageViewController(AboutPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AboutPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        var appIcon = new UIImageView
        {
            Image = AppIconImage(),
            ContentMode = UIViewContentMode.ScaleAspectFit,
            ClipsToBounds = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        appIcon.Layer.CornerRadius = 12;
        appIcon.Hidden = appIcon.Image is null;
        WinoLayout.Size(appIcon, 56, 56);
        var name = WinoStyle.Label("Wino Mail", WinoStyle.Heading, WinoStyle.PrimaryText);
        var description = WinoStyle.Label(Translator.SettingsAboutWino_AppDescription, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var version = WinoStyle.Label(Translator.SettingsAboutVersion + vm.VersionName, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var hero = WinoLayout.HStack(WinoStyle.Space4, appIcon, WinoLayout.VStack(2, name, description, version));
        hero.Alignment = UIStackViewAlignment.Center;
        Add(hero);

        AddGroup(null,
            CommandCard(Translator.SettingsWhatsNew_Title, Translator.SettingsWhatsNew_Description, WinoIconGlyph.Announcement, vm.OpenWhatsNewCommand));

        var links = Group(null,
            Link(Translator.SettingsWebsite_Title, Translator.SettingsWebsite_Description, WinoIconGlyph.Globe, () => vm.WebsiteUrl),
            Link(Translator.SettingsAboutGithub_Title, Translator.SettingsAboutGithub_Description, WinoIconGlyph.GitHub, () => vm.GitHubUrl),
            Link(Translator.SettingsDiscord_Title, Translator.SettingsDiscord_Description, WinoIconGlyph.Discord, () => vm.DiscordChannelUrl));
        if (vm.IsMicrosoftStoreAvailable)
            links.Add(Link(Translator.SettingsStore_Title, Translator.SettingsStore_Description, WinoIconGlyph.MicrosoftStore, () => "Store"));
        links.Add(Link(Translator.SettingsPaypal_Title, Translator.SettingsPaypal_Description, WinoIconGlyph.PayPal, () => vm.PaypalUrl));
        links.Add(Link(Translator.SettingsPrivacyPolicy_Title, Translator.SettingsPrivacyPolicy_Description, WinoIconGlyph.Eye, () => vm.PrivacyPolicyUrl));
        Add(links);

        // Phone width: the switch stays trailing; the log buttons and the identifier go under the text.
        var logsCard = Card(Translator.SettingsEnableLogs_Title, Translator.SettingsEnableLogs_Description, WinoIconGlyph.None,
            Bind.Switch(p, nameof(p.IsLoggingEnabled), s => s.IsLoggingEnabled, (s, v) => s.IsLoggingEnabled = v, Translator.SettingsEnableLogs_Title));
        logsCard.BottomContent = Row(
            Bind.Button(Translator.Buttons_Share, vm.ShareWinoLogCommand),
            Bind.Button(Translator.Buttons_UploadLogs, vm.UploadWinoLogsCommand),
            WinoLayout.Spacer());

        var copy = Bind.Button(string.Empty, vm.CopyDiagnosticIdCommand, icon: WinoIconGlyph.Copy);
        WinoAccessibility.Label(copy, Translator.SettingsDiagnostics_DiagnosticId_Title);
        WinoLayout.Size(copy, 44, 44);
        var diagnosticCard = Card(Translator.SettingsDiagnostics_DiagnosticId_Title, Translator.SettingsDiagnostics_DiagnosticId_Description, WinoIconGlyph.None, copy);
        diagnosticCard.BottomContent = Bind.Label(p, nameof(p.DiagnosticId), s => s.DiagnosticId, WinoStyle.Description, WinoStyle.SecondaryText, 0);

        AddGroup(null, Expander(Translator.SettingsDiagnostics_Title, Translator.SettingsDiagnostics_Description, WinoIconGlyph.Bug, null,
            logsCard, diagnosticCard));
    }

    private WinoSettingsCard Link(string header, string description, WinoIconGlyph icon, Func<string> url)
    {
        var card = CommandCard(header, description, icon, ViewModel.NavigateCommand, () => url());
        card.SetActionIcon(WinoIconGlyph.Open);
        return card;
    }

    /// <summary>The primary app icon named by the bundle's CFBundleIcons, or null.</summary>
    private static UIImage? AppIconImage()
    {
        if (NSBundle.MainBundle.InfoDictionary is not { } info) return null;
        if (info["CFBundleIcons"] is not NSDictionary icons) return null;
        if (icons["CFBundlePrimaryIcon"] is not NSDictionary primary) return null;
        if (primary["CFBundleIconFiles"] is not NSArray { Count: > 0 } files) return null;
        var last = files.GetItem<NSString>(files.Count - 1);
        return last is null ? null : UIImage.FromBundle(last.ToString());
    }
}
