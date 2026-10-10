using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;
using Wino.Mail.ViewModels;
using Wino.Presentation.AppKit;
#if !WINO_APPSTORE
using Wino.Core.MacOS.Bindings.Sparkle;
#endif

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>General: display language, open at login, close behaviour and, in DMG builds, automatic updates (Windows AppPreferencesPage).</summary>
public sealed class AppPreferencesPageViewController(AppPreferencesPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<AppPreferencesPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;

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

        AddGroup(null,
            Card(Translator.SettingsLanguage_Title, Translator.SettingsLanguage_Description, WinoIconGlyph.Translate, language),
            Card(Translator.SettingsAppPreferences_StartupBehavior_Title, Translator.SettingsAppPreferences_StartupBehavior_Description, WinoIconGlyph.Power, startup),
            Card(Translator.SettingsAppPreferences_CloseBehavior_Title, Translator.SettingsAppPreferences_CloseBehavior_Description, WinoIconGlyph.Desktop, closeBehavior));
#if !WINO_APPSTORE
        AddUpdateCards();
#endif
        // Mac App Store builds have no update cards: the App Store updates the app.
    }

#if !WINO_APPSTORE
    /// <summary>Sparkle's automatic-update settings (DMG builds). Sparkle stores them in the app's defaults.</summary>
    private void AddUpdateCards()
    {
        if (!SparkleUpdater.IsStarted) return;

        var install = new NSSwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        var check = new NSSwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        void Refresh()
        {
            check.State = SparkleUpdater.AutomaticallyChecksForUpdates ? 1 : 0;
            install.State = SparkleUpdater.AutomaticallyDownloadsUpdates ? 1 : 0;
            // Installing without asking needs automatic checks and write access to the app's folder.
            install.Enabled = SparkleUpdater.AutomaticallyChecksForUpdates && SparkleUpdater.AllowsAutomaticUpdates;
        }
        check.Activated += (_, _) => { SparkleUpdater.AutomaticallyChecksForUpdates = check.State == 1; Refresh(); };
        install.Activated += (_, _) => { SparkleUpdater.AutomaticallyDownloadsUpdates = install.State == 1; Refresh(); };
        Refresh();

        var checkNow = new NSButton
        {
            Title = Translator.MacOS_SettingsUpdates_CheckNow,
            BezelStyle = NSBezelStyle.Push,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        checkNow.Activated += (_, _) => SparkleUpdater.CheckForUpdates();
        var checkRow = WinoLayout.HStack(WinoStyle.Space4, checkNow, check);
        checkRow.Alignment = NSLayoutAttribute.CenterY;

        AddGroup(null,
            Card(Translator.MacOS_SettingsUpdates_Title, Translator.MacOS_SettingsUpdates_Description, WinoIconGlyph.ArrowDownload, checkRow),
            Card(Translator.MacOS_SettingsUpdatesAutoInstall_Title, Translator.MacOS_SettingsUpdatesAutoInstall_Description, WinoIconGlyph.None, install));
    }
#endif

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
        => ViewModel.InitializeNavigationAsync(mode, parameter!);
}
