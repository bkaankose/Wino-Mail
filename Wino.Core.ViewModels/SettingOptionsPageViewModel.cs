using System;
using System.Collections.Generic;
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
                _ => throw new NotImplementedException()
            };

            Messenger.Send(new BreadcrumbNavigationRequested(pageTitle, pageType));
        }
    }

    [ObservableProperty]
    private List<SettingOption> _settingOptions = new();

    public SettingOptionsPageViewModel(ISettingsBuilderService settingsBuilderService)
    {
        _settingsBuilderService = settingsBuilderService;

        ReloadSettings();
    }

    private void ReloadSettings()
    {
        SettingOptions = _settingsBuilderService.GetSettingItems();
    }
}
