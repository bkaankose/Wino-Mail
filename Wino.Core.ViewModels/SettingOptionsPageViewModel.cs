using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public partial class SettingOptionsPageViewModel : CoreBaseViewModel
{
    private readonly ISettingsBuilderService _settingsBuilderService;
    private readonly ObservableGroupedCollection<SettingOptionCategory, SettingOption> _internalGroupedSettings;

    [RelayCommand]
    private void GoAccountSettings() => Messenger.Send<NavigateManageAccountsRequested>();

    [RelayCommand]
    public void NavigateSubDetail(object type)
    {
        if (type is WinoPage pageType)
        {
            if (pageType == WinoPage.AccountManagementPage)
            {
                GoAccountSettings();
                return;
            }

            string pageTitle = pageType switch
            {
                WinoPage.PersonalizationPage => Translator.SettingsPersonalization_Title,
                WinoPage.AboutPage => Translator.SettingsAbout_Title,
                WinoPage.MessageListPage => Translator.SettingsMessageList_Title,
                WinoPage.ReadComposePanePage => Translator.SettingsReadComposePane_Title,
                WinoPage.LanguageTimePage => Translator.SettingsLanguageTime_Title,
                WinoPage.AppPreferencesPage => Translator.SettingsAppPreferences_Title,
                WinoPage.CalendarSettingsPage => Translator.SettingsCalendarSettings_Title,
                WinoPage.SignatureAndEncryptionPage => Translator.SettingsSignatureAndEncryption_Title,
                WinoPage.KeyboardShortcutsPage => Translator.Settings_KeyboardShortcuts_Title,
                _ => throw new NotImplementedException()
            };

            Messenger.Send(new BreadcrumbNavigationRequested(pageTitle, pageType));
        }
    }

    [ObservableProperty]
    public partial ReadOnlyObservableGroupedCollection<SettingOptionCategory, SettingOption> SettingOptions { get; set; }

    public SettingOptionsPageViewModel(ISettingsBuilderService settingsBuilderService)
    {
        _settingsBuilderService = settingsBuilderService;
        _internalGroupedSettings = new ObservableGroupedCollection<SettingOptionCategory, SettingOption>();
        SettingOptions = new ReadOnlyObservableGroupedCollection<SettingOptionCategory, SettingOption>(_internalGroupedSettings);

        ReloadSettings();
    }

    private void ReloadSettings()
    {
        var settings = _settingsBuilderService.GetSettingItems();
        var grouped = settings.GroupBy(x => x.Category);
        
        _internalGroupedSettings.Clear();
        foreach (var group in grouped)
        {
            _internalGroupedSettings.AddGroup(group.Key, group);
        }
    }
}
