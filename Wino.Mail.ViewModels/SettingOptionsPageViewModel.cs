using System;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Messages.Navigation;

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
