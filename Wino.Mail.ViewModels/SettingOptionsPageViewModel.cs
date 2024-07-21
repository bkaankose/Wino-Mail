using System;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Domain;
using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Navigation;
using Wino.Messaging.Client.Navigation;

namespace Wino.Mail.ViewModels
{
    public partial class SettingOptionsPageViewModel : BaseViewModel
    {
        public SettingOptionsPageViewModel(IDialogService dialogService) : base(dialogService) { }

        [RelayCommand]
        private void GoAccountSettings() => Messenger.Send<NavigateSettingsRequested>();

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);
        }

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
                    WinoPage.ReadingPanePage => Translator.SettingsReadingPane_Title,
                    WinoPage.LanguageTimePage => Translator.SettingsLanguageTime_Title,
                    _ => throw new NotImplementedException()
                };

                Messenger.Send(new BreadcrumbNavigationRequested(pageTitle, pageType));
            }
        }
    }
}
