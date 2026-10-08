using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>General: display language, open at login, close behaviour and update notifications (Windows AppPreferencesPage).</summary>
public sealed class AppPreferencesPageViewController(AppPreferencesPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AppPreferencesPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var preferences = vm.PreferencesService;

        var language = Bind.PopUp<AppPreferencesPageViewModel, AppLanguageModel>(vm,
            source => source.AvailableLanguages, item => item.DisplayName,
            nameof(vm.SelectedLanguage), source => source.SelectedLanguage, (source, value) => source.SelectedLanguage = value,
            itemsProperty: nameof(vm.AvailableLanguages), width: 180);

        var startup = Bind.CommandSwitch(vm, nameof(vm.IsStartupBehaviorEnabled), source => source.IsStartupBehaviorEnabled,
            vm.ToggleStartupBehaviorCommand, Translator.SettingsAppPreferences_StartupBehavior_Title);

        var closeBehavior = Bind.PopUp<AppPreferencesPageViewModel, string>(vm,
            source => source.CloseBehaviorModes, item => item,
            nameof(vm.SelectedCloseBehaviorMode), source => source.SelectedCloseBehaviorMode, (source, value) => source.SelectedCloseBehaviorMode = value,
            itemsProperty: nameof(vm.CloseBehaviorModes), width: 180);

        var updates = Bind.Switch(preferences, nameof(preferences.IsStoreUpdateNotificationsEnabled),
            source => source.IsStoreUpdateNotificationsEnabled, (source, value) => source.IsStoreUpdateNotificationsEnabled = value,
            Translator.SettingsAppPreferences_StoreUpdateNotifications_Title);

        AddGroup(null,
            Card(Translator.SettingsLanguage_Title, Translator.SettingsLanguage_Description, WinoIconGlyph.Translate, language),
            Card(Translator.SettingsAppPreferences_StartupBehavior_Title, Translator.SettingsAppPreferences_StartupBehavior_Description, WinoIconGlyph.Power, startup),
            Card(Translator.SettingsAppPreferences_CloseBehavior_Title, Translator.SettingsAppPreferences_CloseBehavior_Description, WinoIconGlyph.Desktop, closeBehavior),
            Card(Translator.SettingsAppPreferences_StoreUpdateNotifications_Title, Translator.SettingsAppPreferences_StoreUpdateNotifications_Description, WinoIconGlyph.ArrowDownload, updates));
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter!);
}
