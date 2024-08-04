using System;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels
{
    public partial class SettingOptionsPageViewModel(IDialogService dialogService) : BaseViewModel(dialogService)
    {
        [RelayCommand]
        private void GoAccountSettings() => Messenger.Send<NavigateSettingsRequested>();

        [RelayCommand]
        public void NavigateSubDetail(object type)
        {
            if (type is WinoPage pageType)
            {
                string pageTitle = pageType switch
                {
                    WinoPage.PersonalizationPage => Translator.SettingsPersonalization_Title,
                    WinoPage.AboutPage => Translator.SettingsAbout_Title,
                    WinoPage.MessageListPage => Translator.SettingsMessageList_Title,
                    WinoPage.ReadComposePanePage => Translator.SettingsReadComposePane_Title,
                    WinoPage.LanguageTimePage => Translator.SettingsLanguageTime_Title,
                    WinoPage.AppPreferencesPage => Translator.SettingsAppPreferences_Title,
                    _ => throw new NotImplementedException()
                };

                Messenger.Send(new BreadcrumbNavigationRequested(pageTitle, pageType));
            }
        }
    }
}
