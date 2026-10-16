using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.ViewModels;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// General: the display language (Windows AppPreferencesPage). iOS has no launch-at-sign-in or close behaviour (the
/// system owns app lifetime), and the App Store updates the app, so the Mac startup, close and update cards are absent.
/// </summary>
public sealed class AppPreferencesPageViewController(AppPreferencesPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AppPreferencesPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        var language = Bind.PopUp<AppPreferencesPageViewModel, AppLanguageModel>(vm,
            source => source.AvailableLanguages, item => item.DisplayName,
            nameof(vm.SelectedLanguage), source => source.SelectedLanguage, (source, value) => source.SelectedLanguage = value,
            itemsProperty: nameof(vm.AvailableLanguages));

        AddGroup(null,
            Card(Translator.SettingsLanguage_Title, Translator.SettingsLanguage_Description, WinoIconGlyph.Translate, language));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter);
}
