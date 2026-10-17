using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Settings.Pages;

/// <summary>
/// General: the display language (Windows AppPreferencesPage). Android has no launch at sign-in or close behaviour
/// (the system owns the app's lifetime, IPlatformCapabilities.StartupIntegration and Tray are false) and Google Play
/// updates the app, so the startup, close and update settings are unavailable, as on iOS
/// (SettingAvailability: LaunchAtSignIn, CloseBehavior, StoreUpdateNotifications).
/// </summary>
public sealed class AppPreferencesPageFragment : SettingsPageFragment<AppPreferencesPageViewModel>
{
    // The catalogue's Android description of General is the language card's own text; the card says it once.
    protected override bool ShowsPageDescription => false;

    protected override void BuildPage()
    {
        var vm = VM;
        var language = Bind.PopUp<AppPreferencesPageViewModel, AppLanguageModel>(vm,
            source => source.AvailableLanguages, item => item.DisplayName,
            nameof(vm.SelectedLanguage), source => source.SelectedLanguage, (source, value) => source.SelectedLanguage = value,
            itemsProperty: nameof(vm.AvailableLanguages));

        AddGroup(null,
            StackedCard(Translator.SettingsLanguage_Title, Translator.SettingsLanguage_Description, WinoIconGlyph.Translate, language));
    }
}
