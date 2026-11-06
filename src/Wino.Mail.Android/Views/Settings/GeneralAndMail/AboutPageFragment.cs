using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.GeneralAndMail;

/// <summary>
/// About: app identity, links, diagnostics and version (Windows AboutPage, iOS AboutPageViewController, same card order).
/// The Microsoft Store link follows the ViewModel's store availability, which is false on Android.
/// </summary>
public sealed class AboutPageFragment : SettingsPageFragment<AboutPageViewModel>
{
    protected override void BuildPage()
    {
        var vm = VM;
        var p = vm.PreferencesService;

        var appIcon = new ImageView(Ctx);
        appIcon.SetImageDrawable(AppIcon());
        appIcon.Visibility = appIcon.Drawable is null ? ViewStates.Gone : ViewStates.Visible;
        appIcon.ImportantForAccessibility = ImportantForAccessibility.No;
        var name = Text("Wino Mail", WinoTextStyle.Title);
        var description = Text(Translator.SettingsAboutWino_AppDescription, WinoTextStyle.Description);
        var version = Text(Translator.SettingsAboutVersion + vm.VersionName, WinoTextStyle.Description);
        var texts = Stack(Resource.Dimension.wino_space_3xs, name, description, version);
        var hero = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        hero.SetGravity(GravityFlags.CenterVertical);
        int iconSize = WinoTokens.Px(Ctx, Resource.Dimension.wino_mail_about_icon);
        hero.AddView(appIcon, new LinearLayout.LayoutParams(iconSize, iconSize));
        hero.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_l) });
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
            Bind.Button(Translator.Buttons_UploadLogs, vm.UploadWinoLogsCommand));

        var copy = Bind.IconButton(WinoIconGlyph.Copy, Translator.SettingsDiagnostics_DiagnosticId_Title);
        Bind.Views.Command(copy, vm.CopyDiagnosticIdCommand);
        var diagnosticCard = Card(Translator.SettingsDiagnostics_DiagnosticId_Title, Translator.SettingsDiagnostics_DiagnosticId_Description, WinoIconGlyph.None, copy);
        var identifier = Bind.Label(p, nameof(p.DiagnosticId), s => s.DiagnosticId, maximumLines: 0);
        identifier.SetTextIsSelectable(true);
        diagnosticCard.BottomContent = identifier;

        AddGroup(null, Expander(Translator.SettingsDiagnostics_Title, Translator.SettingsDiagnostics_Description, WinoIconGlyph.Bug, null,
            logsCard, diagnosticCard));
    }

    private WinoSettingsCard Link(string header, string description, WinoIconGlyph icon, Func<string> url)
    {
        var card = CommandCard(header, description, icon, VM.NavigateCommand, () => url());
        card.SetActionIcon(WinoIconGlyph.Open);
        return card;
    }

    /// <summary>The launcher icon of the installed package, or null.</summary>
    private Drawable? AppIcon()
    {
        try
        {
            return Ctx.ApplicationInfo is { } info ? Ctx.PackageManager?.GetApplicationIcon(info) : null;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return null;
        }
    }
}
