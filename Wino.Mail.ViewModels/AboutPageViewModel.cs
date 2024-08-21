using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels
{
    public partial class AboutPageViewModel : BaseViewModel
    {
        private readonly IStoreRatingService _storeRatingService;
        private readonly INativeAppService _nativeAppService;
        private readonly IApplicationConfiguration _appInitializerService;
        private readonly IFileService _fileService;
        private readonly ILogInitializer _logInitializer;

        public string VersionName => _nativeAppService.GetFullAppVersion();
        public string DiscordChannelUrl => "https://discord.gg/windows-apps-hub-714581497222398064";
        public string GitHubUrl => "https://github.com/bkaankose/Wino-Mail/";
        public string PrivacyPolicyUrl => "https://www.winomail.app/privacy_policy.html";
        public string PaypalUrl => "https://paypal.me/bkaankose?country.x=PL&locale.x=en_US";

        public IPreferencesService PreferencesService { get; }

        public AboutPageViewModel(IStoreRatingService storeRatingService,
                                  IDialogService dialogService,
                                  INativeAppService nativeAppService,
                                  IPreferencesService preferencesService,
                                  IApplicationConfiguration appInitializerService,
                                  IFileService fileService,
                                  ILogInitializer logInitializer) : base(dialogService)
        {
            _storeRatingService = storeRatingService;
            _nativeAppService = nativeAppService;
            _logInitializer = logInitializer;
            _appInitializerService = appInitializerService;
            _fileService = fileService;

            PreferencesService = preferencesService;
        }

        protected override void OnActivated()
        {
            base.OnActivated();

            PreferencesService.PreferenceChanged -= PreferencesChanged;
            PreferencesService.PreferenceChanged += PreferencesChanged;
        }

        protected override void OnDeactivated()
        {
            base.OnDeactivated();

            PreferencesService.PreferenceChanged -= PreferencesChanged;
        }

        private void PreferencesChanged(object sender, string e)
        {
            if (e == nameof(PreferencesService.IsLoggingEnabled))
            {
                _logInitializer.RefreshLoggingLevel();
            }
        }

        [RelayCommand]
        private async Task ShareWinoLogAsync()
        {
            var appDataFolder = _appInitializerService.ApplicationDataFolderPath;

            var selectedFolderPath = await DialogService.PickWindowsFolderAsync();

            if (string.IsNullOrEmpty(selectedFolderPath)) return;

            var areLogsSaved = await _fileService.SaveLogsToFolderAsync(appDataFolder, selectedFolderPath).ConfigureAwait(false);

            if (areLogsSaved)
            {
                DialogService.InfoBarMessage(Translator.Info_LogsSavedTitle, string.Format(Translator.Info_LogsSavedMessage, Constants.LogArchiveFileName), InfoBarMessageType.Success);
            }
            else
            {
                DialogService.InfoBarMessage(Translator.Info_LogsNotFoundTitle, Translator.Info_LogsNotFoundMessage, InfoBarMessageType.Error);
            }
        }

        [RelayCommand]
        private async Task Navigate(object url)
        {
            if (url is string stringUrl)
            {
                if (stringUrl == "Store")
                    await ShowRateDialogAsync();
                else
                {
                    // Discord disclaimer message about server.
                    if (stringUrl == DiscordChannelUrl)
                        await DialogService.ShowMessageAsync(Translator.DiscordChannelDisclaimerMessage, Translator.DiscordChannelDisclaimerTitle);

                    await _nativeAppService.LaunchUriAsync(new Uri(stringUrl));
                }
            }
        }

        private Task ShowRateDialogAsync() => _storeRatingService.LaunchStorePageForReviewAsync();
    }
}
