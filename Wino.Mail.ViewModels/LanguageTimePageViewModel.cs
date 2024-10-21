using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;

namespace Wino.Mail.ViewModels
{
    public partial class LanguageTimePageViewModel(IPreferencesService preferencesService, ITranslationService translationService) : MailBaseViewModel
    {
        public IPreferencesService PreferencesService { get; } = preferencesService;
        private readonly ITranslationService _translationService = translationService;

        [ObservableProperty]
        private List<AppLanguageModel> _availableLanguages;

        [ObservableProperty]
        private AppLanguageModel _selectedLanguage;

        private bool isInitialized = false;

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            AvailableLanguages = _translationService.GetAvailableLanguages();

            SelectedLanguage = AvailableLanguages.FirstOrDefault(a => a.Language == PreferencesService.CurrentLanguage);

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
    }
}
