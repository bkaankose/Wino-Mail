using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Infrastructure.Updates;
using Wino.Presentation.Linux;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// General: display language, launch at sign-in and close behaviour (Windows AppPreferencesPage), and in a packaged copy
/// the update check (macOS AppPreferencesPageViewController).
/// </summary>
internal sealed class AppPreferencesPage(AppPreferencesPageViewModel viewModel, WinoViewServices services, LinuxUpdateService updates,
    LinuxUpdatePresenter updatePresenter)
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
                : BackgroundRow(vm.PreferencesService),
            UpdatesRow());
    }

    private Adw.SwitchRow? UpdatesRow()
    {
        if (!updates.IsAvailable) return null;
        var row = WinoSettingsRows.Switch(Translator.MacOS_SettingsUpdates_Title, Translator.MacOS_SettingsUpdates_Description, WinoIconGlyph.ArrowDownload);
        row.SetActive(updates.AutomaticCheck);
        var check = Gtk.Button.NewWithLabel(Translator.MacOS_SettingsUpdates_CheckNow);
        check.SetValign(Gtk.Align.Center);
        WinoSettingsRows.Suffix(row, check);
        Bindings.Own(GtkSignals.OnNotify(row, "active", () => updates.AutomaticCheck = row.GetActive()));
        Bindings.Own(GtkSignals.OnClicked(check, () => _ = updatePresenter.CheckNowAsync()));
        return row;
    }

    // The shared row needs a tray, which stock GNOME has not; Linux offers background or quit and reads a stored
    // tray choice as background. The row description mentions the tray, so the chosen option's description is shown.
    // A drop-down suffix keeps the whole value readable; a combo row ellipsized it beside the long subtitle.
    private Adw.ActionRow BackgroundRow(IPreferencesService preferences)
    {
        var row = WinoSettingsRows.Action(Translator.SettingsAppPreferences_CloseBehavior_Title, null, WinoIconGlyph.Desktop);
        var choice = WinoSettingsRows.Suffix(row, Settings.ChoiceSuffix(preferences, Translator.SettingsAppPreferences_CloseBehavior_Title,
            [Translator.SettingsAppPreferences_ServerBackgroundingMode_Invisible_Title, Translator.SettingsAppPreferences_ServerBackgroundingMode_Terminate_Title],
            nameof(IPreferencesService.AppCloseBehavior), static p => p.AppCloseBehavior == AppCloseBehavior.Terminate ? 1 : 0,
            static (p, index) => p.AppCloseBehavior = index == 1 ? AppCloseBehavior.Terminate : AppCloseBehavior.RunInBackgroundWithoutTrayIcon));
        row.SetActivatableWidget(choice);
        return Settings.Subtitle(row, preferences, nameof(IPreferencesService.AppCloseBehavior), static p => p.AppCloseBehavior == AppCloseBehavior.Terminate
            ? Translator.SettingsAppPreferences_ServerBackgroundingMode_Terminate_Description
            : Translator.SettingsAppPreferences_ServerBackgroundingMode_Invisible_Description);
    }

    protected override Task NavigateAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeNavigationAsync(mode, parameter);
}
