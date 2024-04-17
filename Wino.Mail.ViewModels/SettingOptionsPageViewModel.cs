using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;
using Wino.Core.Messages.Navigation;

namespace Wino.Mail.ViewModels
{
    public partial class SettingOptionsPageViewModel : BaseViewModel
    {
        private readonly ITranslationService _translationService;
        private readonly IPreferencesService _preferencesService;

        [ObservableProperty]
        private List<AppLanguageModel> _availableLanguages;

        [ObservableProperty]
        private AppLanguageModel _selectedLanguage;

        private bool isInitialized = false;

        public SettingOptionsPageViewModel(IDialogService dialogService,
                                           ITranslationService translationService,
                                           IPreferencesService preferencesService) : base(dialogService)
        {
            _translationService = translationService;
            _preferencesService = preferencesService;
        }

        [RelayCommand]
        private void GoAccountSettings() => Messenger.Send<NavigateSettingsRequested>();

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            AvailableLanguages = _translationService.GetAvailableLanguages();

            SelectedLanguage = AvailableLanguages.FirstOrDefault(a => a.Language == _preferencesService.CurrentLanguage);

            isInitialized = true;
        }

        protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (!isInitialized) return;

            if (e.PropertyName == nameof(SelectedLanguage))
            {
                await _translationService.InitializeLanguageAsync(SelectedLanguage.Language);
            }
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

                Messenger.Send(new BreadcrumbNavigationRequested(pageTitle, pageType));
            }
        }
    }
}
