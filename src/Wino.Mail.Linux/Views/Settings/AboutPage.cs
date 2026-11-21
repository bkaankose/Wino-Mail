using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Common;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>About (Windows AboutPage): the app, What's New, the links and diagnostics.</summary>
internal sealed class AboutPage(AboutPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<AboutPageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var p = vm.PreferencesService;

        AddGroup(null, Hero(vm));

        AddGroup(null, Settings.Command(Translator.SettingsWhatsNew_Title, Translator.SettingsWhatsNew_Description, WinoIconGlyph.Announcement, vm.OpenWhatsNewCommand));

        AddGroup(null,
            Link(Translator.SettingsWebsite_Title, Translator.SettingsWebsite_Description, WinoIconGlyph.Globe, vm.WebsiteUrl),
            Link(Translator.SettingsAboutGithub_Title, Translator.SettingsAboutGithub_Description, WinoIconGlyph.GitHub, vm.GitHubUrl),
            Link(Translator.SettingsDiscord_Title, Translator.SettingsDiscord_Description, WinoIconGlyph.Discord, vm.DiscordChannelUrl),
            vm.IsMicrosoftStoreAvailable ? Link(Translator.SettingsStore_Title, Translator.SettingsStore_Description, WinoIconGlyph.MicrosoftStore, "Store") : null,
            Link(Translator.SettingsPaypal_Title, Translator.SettingsPaypal_Description, WinoIconGlyph.PayPal, vm.PaypalUrl),
            Link(Translator.SettingsPrivacyPolicy_Title, Translator.SettingsPrivacyPolicy_Description, WinoIconGlyph.Eye, vm.PrivacyPolicyUrl));

        var logs = WinoSettingsRows.Action(Translator.SettingsEnableLogs_Title, Translator.SettingsEnableLogs_Description);
        WinoSettingsRows.Suffix(logs, Settings.Button(Translator.Buttons_Share, vm.ShareWinoLogCommand));
        WinoSettingsRows.Suffix(logs, Settings.Button(Translator.Buttons_UploadLogs, vm.UploadWinoLogsCommand));
        WinoSettingsRows.Suffix(logs, Settings.SwitchSuffix(p, Translator.SettingsEnableLogs_Title, nameof(p.IsLoggingEnabled),
            static s => s.IsLoggingEnabled, static (s, v) => s.IsLoggingEnabled = v));

        var diagnosticId = WinoSettingsRows.Action(Translator.SettingsDiagnostics_DiagnosticId_Title, Translator.SettingsDiagnostics_DiagnosticId_Description);
        var id = WinoSettingsRows.Suffix(diagnosticId, Settings.LabelSuffix(p, nameof(p.DiagnosticId), static s => s.DiagnosticId));
        id.SetSelectable(true);
        var copy = WinoSettingsRows.Suffix(diagnosticId, WinoSettingsRows.GlyphButton(WinoIconGlyph.Copy, Translator.Buttons_Copy));
        copy.AddCssClass("flat");
        var copyCommand = Bindings.Own(new Wino.Presentation.Bindings.CommandBinding(vm.CopyDiagnosticIdCommand, static () => null, copy.SetSensitive, Dispatcher, ReportError));
        Settings.Own(GtkSignals.OnClicked(copy, copyCommand.Execute));

        AddGroup(null, WinoSettingsRows.Expander(Translator.SettingsDiagnostics_Title, Translator.SettingsDiagnostics_Description, WinoIconGlyph.Bug, false,
            logs, diagnosticId));
    }

    private Adw.ActionRow Link(string title, string description, WinoIconGlyph icon, string url)
        => Settings.Command(title, description, icon, ViewModel.NavigateCommand, () => url, external: true);

    private static Adw.PreferencesRow Hero(AboutPageViewModel vm)
    {
        var icon = new WinoIcon(WinoIconGlyph.Mail, 48).Root;
        icon.AddCssClass("accent");
        icon.SetValign(Gtk.Align.Center);

        var name = Gtk.Label.New(AppActions.ApplicationName);
        name.AddCssClass("title-3");
        name.SetXalign(0);
        var description = WinoSettingsRows.Caption(Translator.SettingsAboutWino_AppDescription);
        var version = WinoSettingsRows.Caption(Translator.SettingsAboutVersion + vm.VersionName);
        version.SetSelectable(true);

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.Append(name);
        text.Append(description);
        text.Append(version);

        var hero = Gtk.Box.New(Gtk.Orientation.Horizontal, 16);
        hero.Append(icon);
        hero.Append(text);
        return WinoSettingsRows.Custom(hero);
    }
}
