using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>General: display language, launch at sign-in and close behaviour (Windows AppPreferencesPage).</summary>
internal sealed class AppPreferencesPage(AppPreferencesPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<AppPreferencesPageViewModel>(viewModel, services)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

        AddGroup(null,
            Settings.Choice<AppPreferencesPageViewModel, AppLanguageModel>(vm, Translator.SettingsLanguage_Title, Translator.SettingsLanguage_Description, WinoIconGlyph.Translate,
                static s => s.AvailableLanguages, static item => item.DisplayName,
                nameof(vm.SelectedLanguage), static s => s.SelectedLanguage, static (s, v) => s.SelectedLanguage = v,
                itemsProperty: nameof(vm.AvailableLanguages)),
            vm.IsLaunchAtSignInAvailable
                ? Settings.CommandSwitch(vm, Translator.SettingsAppPreferences_LaunchAtSignIn_Title, Translator.SettingsAppPreferences_LaunchAtSignIn_Description, WinoIconGlyph.Power,
                    nameof(vm.IsStartupBehaviorEnabled), static s => s.IsStartupBehaviorEnabled, vm.ToggleStartupBehaviorCommand)
                : null,
            vm.IsCloseBehaviorAvailable
                ? Settings.Choice<AppPreferencesPageViewModel, string>(vm, Translator.SettingsAppPreferences_CloseBehavior_Title, Translator.SettingsAppPreferences_CloseBehavior_Description, WinoIconGlyph.Desktop,
                    static s => s.CloseBehaviorModes, static item => item,
                    nameof(vm.SelectedCloseBehaviorMode), static s => s.SelectedCloseBehaviorMode, static (s, v) => s.SelectedCloseBehaviorMode = v,
                    itemsProperty: nameof(vm.CloseBehaviorModes))
                : null);
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);
}
