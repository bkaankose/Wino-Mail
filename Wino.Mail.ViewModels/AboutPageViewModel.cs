using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Mail.ViewModels
{
    public class AboutPageViewModel : BaseViewModel
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

        public AsyncRelayCommand<object> NavigateCommand { get; set; }
        public AsyncRelayCommand ShareWinoLogCommand { get; set; }
        public AsyncRelayCommand ShareProtocolLogCommand { get; set; }
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
            NavigateCommand = new AsyncRelayCommand<object>(Navigate);
            ShareWinoLogCommand = new AsyncRelayCommand(ShareWinoLogAsync);
            ShareProtocolLogCommand = new AsyncRelayCommand(ShareProtocolLogAsync);
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

        private Task ShareProtocolLogAsync()
            => SaveLogInternalAsync(ImapTestService.ProtocolLogFileName);

        private Task ShareWinoLogAsync()
            => SaveLogInternalAsync(Constants.ClientLogFile);

        private async Task SaveLogInternalAsync(string sourceFileName)
        {
            var appDataFolder = _appInitializerService.ApplicationDataFolderPath;

            var logFile = Path.Combine(appDataFolder, sourceFileName);

            if (!File.Exists(logFile))
            {
                DialogService.InfoBarMessage(Translator.Info_LogsNotFoundTitle, Translator.Info_LogsNotFoundMessage, Core.Domain.Enums.InfoBarMessageType.Warning);
                return;
            }

            var selectedFolderPath = await DialogService.PickWindowsFolderAsync();

            if (string.IsNullOrEmpty(selectedFolderPath)) return;

            var copiedFilePath = await _fileService.CopyFileAsync(logFile, selectedFolderPath);

            var copiedFileName = Path.GetFileName(copiedFilePath);

            DialogService.InfoBarMessage(Translator.Info_LogsSavedTitle, string.Format(Translator.Info_LogsSavedMessage, copiedFileName), Core.Domain.Enums.InfoBarMessageType.Success);
        }

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
