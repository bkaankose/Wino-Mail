using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
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
            if (type is string stringParameter)
            {
                WinoPage pageType = default;

                string pageTitle = stringParameter;

                // They are just params and don't have to be localized. Don't change.
                if (stringParameter == "Personalization")
                    pageType = WinoPage.PersonalizationPage;
                else if (stringParameter == "About")
                    pageType = WinoPage.AboutPage;
                else if (stringParameter == "Message List")
                    pageType = WinoPage.MessageListPage;
                else if (stringParameter == "Reading Pane")
                    pageType = WinoPage.ReadingPanePage;
                else if (stringParameter == "Language And Time")
                    pageType = WinoPage.LanguageTimePage;

                Messenger.Send(new BreadcrumbNavigationRequested(pageTitle, pageType));
            }
        }
    }
}
