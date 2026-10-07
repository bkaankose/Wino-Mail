using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>About: app identity, links, diagnostics and version (Windows AboutPage, same card order).</summary>
public sealed class AboutPageViewController(AboutPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AboutPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        var appIcon = new NSImageView
        {
            Image = NSApplication.SharedApplication.ApplicationIconImage,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(appIcon, 56, 56);
        var name = WinoStyle.Label("Wino Mail", WinoStyle.Heading, WinoStyle.PrimaryText);
        var description = WinoStyle.Label(Translator.SettingsAboutWinoDescription, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        var version = WinoStyle.Label(Translator.SettingsAboutVersion + vm.VersionName, WinoStyle.Description, WinoStyle.SecondaryText);
        version.Selectable = true;
        var hero = WinoLayout.HStack(WinoStyle.Space4, appIcon, WinoLayout.VStack(2, name, description, version));
        hero.Alignment = NSLayoutAttribute.CenterY;
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

        var logs = Row(
            Bind.Button(Translator.Buttons_Share, vm.ShareWinoLogCommand),
            Bind.Button(Translator.Buttons_UploadLogs, vm.UploadWinoLogsCommand),
            Bind.Switch(p, nameof(p.IsLoggingEnabled), s => s.IsLoggingEnabled, (s, v) => s.IsLoggingEnabled = v, Translator.SettingsEnableLogs_Title));
        var diagnosticId = Row(
            Bind.Label(p, nameof(p.DiagnosticId), s => s.DiagnosticId, WinoStyle.Description, WinoStyle.SecondaryText),
            Bind.Button(string.Empty, vm.CopyDiagnosticIdCommand, icon: WinoIconGlyph.Copy));
        AddGroup(null, Expander(Translator.SettingsDiagnostics_Title, Translator.SettingsDiagnostics_Description, WinoIconGlyph.Bug, null,
            Card(Translator.SettingsEnableLogs_Title, Translator.SettingsEnableLogs_Description, WinoIconGlyph.None, logs),
            Card(Translator.SettingsDiagnostics_DiagnosticId_Title, Translator.SettingsDiagnostics_DiagnosticId_Description, WinoIconGlyph.None, diagnosticId)));
    }

    private WinoSettingsCard Link(string header, string description, WinoIconGlyph icon, Func<string> url)
    {
        var card = CommandCard(header, description, icon, ViewModel.NavigateCommand, () => url());
        card.SetActionIcon(WinoIconGlyph.Open);
        return card;
    }
}
