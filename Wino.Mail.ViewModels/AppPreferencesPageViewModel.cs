using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AppCenter.Crashes;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels
{
    public partial class AppPreferencesPageViewModel : BaseViewModel
    {
        private IPreferencesService PreferencesService { get; }

        [ObservableProperty]
        private bool isUpdateNotificationEnabled;
        private bool IsUpdateNotificationEnabledChangedManually;

        [ObservableProperty]
        private List<string> _appTerminationBehavior;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsStartupBehaviorDisabled))]
        [NotifyPropertyChangedFor(nameof(IsStartupBehaviorEnabled))]
        private StartupBehaviorResult startupBehaviorResult;

        public bool IsStartupBehaviorDisabled => !IsStartupBehaviorEnabled;
        public bool IsStartupBehaviorEnabled => StartupBehaviorResult == StartupBehaviorResult.Enabled;

        private string _selectedAppTerminationBehavior;
        public string SelectedAppTerminationBehavior
        {
            get => _selectedAppTerminationBehavior;
            set
            {
                SetProperty(ref _selectedAppTerminationBehavior, value);

                PreferencesService.ServerTerminationBehavior = (ServerBackgroundMode)AppTerminationBehavior.IndexOf(value);
            }
        }

        private readonly IWinoServerConnectionManager _winoServerConnectionManager;
        private readonly IStartupBehaviorService _startupBehaviorService;

        private readonly IBackgroundTaskService _backgroundTaskService;

        public AppPreferencesPageViewModel(IDialogService dialogService,
                                           IPreferencesService preferencesService,
                                           IWinoServerConnectionManager winoServerConnectionManager,
                                           IStartupBehaviorService startupBehaviorService,
                                           IBackgroundTaskService backgroundTaskService) : base(dialogService)
        {
            PreferencesService = preferencesService;
            _winoServerConnectionManager = winoServerConnectionManager;
            _startupBehaviorService = startupBehaviorService;
            _backgroundTaskService = backgroundTaskService;

            // Load the app termination behavior options

            _appTerminationBehavior =
            [
                Translator.SettingsAppPreferences_ServerBackgroundingMode_MinimizeTray_Title, // "Minimize to tray"
                Translator.SettingsAppPreferences_ServerBackgroundingMode_Invisible_Title, // "Invisible"
                Translator.SettingsAppPreferences_ServerBackgroundingMode_Terminate_Title // "Terminate"
            ];

            SelectedAppTerminationBehavior = _appTerminationBehavior[(int)PreferencesService.ServerTerminationBehavior];
        }

        [RelayCommand]
        private async Task ToggleStartupBehaviorAsync()
        {
            if (IsStartupBehaviorEnabled)
            {
                await DisableStartupAsync();
            }
            else
            {
                await EnableStartupAsync();
            }

            OnPropertyChanged(nameof(IsStartupBehaviorEnabled));
        }

        private async Task EnableStartupAsync()
        {
            StartupBehaviorResult = await _startupBehaviorService.ToggleStartupBehavior(true);

            NotifyCurrentStartupState();
        }

        private async Task DisableStartupAsync()
        {
            StartupBehaviorResult = await _startupBehaviorService.ToggleStartupBehavior(false);

            NotifyCurrentStartupState();
        }

        private void NotifyCurrentStartupState()
        {
            if (StartupBehaviorResult == StartupBehaviorResult.Enabled)
            {
                DialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsAppPreferences_StartupBehavior_Enabled, InfoBarMessageType.Success);
            }
            else if (StartupBehaviorResult == StartupBehaviorResult.Disabled)
            {
                DialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsAppPreferences_StartupBehavior_Disabled, InfoBarMessageType.Warning);
            }
            else if (StartupBehaviorResult == StartupBehaviorResult.DisabledByPolicy)
            {
                DialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsAppPreferences_StartupBehavior_DisabledByPolicy, InfoBarMessageType.Warning);
            }
            else if (StartupBehaviorResult == StartupBehaviorResult.DisabledByUser)
            {
                DialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsAppPreferences_StartupBehavior_DisabledByUser, InfoBarMessageType.Warning);
            }
            else
            {
                DialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.SettingsAppPreferences_StartupBehavior_FatalError, InfoBarMessageType.Error);
            }
        }

        private async Task ConfigureBackgroundTasksAsync()
        {
            if (IsUpdateNotificationEnabledChangedManually) return;

            PreferencesService.IsUpdateNotificationEnabled = IsUpdateNotificationEnabled;

            try
            {
                _backgroundTaskService.UnregisterAllBackgroundTask();
                await _backgroundTaskService.RegisterBackgroundTasksAsync();
            }
            catch (Exception ex)
            {
                // revert to the previous value
                IsUpdateNotificationEnabledChangedManually = true;
                IsUpdateNotificationEnabled = !IsUpdateNotificationEnabled;
                IsUpdateNotificationEnabledChangedManually = false;
                PreferencesService.IsUpdateNotificationEnabled = IsUpdateNotificationEnabled;

                Crashes.TrackError(ex);
                DialogService.InfoBarMessage(Translator.Info_BackgroundExecutionUnknownErrorTitle, Translator.Info_BackgroundExecutionUnknownErrorMessage, InfoBarMessageType.Error);
            }
        }

        protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            switch (e.PropertyName)
            {
                case nameof(IsUpdateNotificationEnabled):
                    await ConfigureBackgroundTasksAsync();
                    break;
                case nameof(SelectedAppTerminationBehavior):
                    var terminationModeChangedResult = await _winoServerConnectionManager.GetResponseAsync<bool, ServerTerminationModeChanged>(new ServerTerminationModeChanged(PreferencesService.ServerTerminationBehavior));

                    if (!terminationModeChangedResult.IsSuccess)
                    {
                        DialogService.InfoBarMessage(Translator.GeneralTitle_Error, terminationModeChangedResult.Message, InfoBarMessageType.Error);
                    }
                    break;
            }
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            IsUpdateNotificationEnabled = PreferencesService.IsUpdateNotificationEnabled;
            StartupBehaviorResult = await _startupBehaviorService.GetCurrentStartupBehaviorAsync();
        }
    }
}
