using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels
{
    public partial class AppPreferencesPageViewModel : BaseViewModel
    {
        public IPreferencesService PreferencesService { get; }

        [ObservableProperty]
        private List<string> _appTerminationBehavior;

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
        public AppPreferencesPageViewModel(IDialogService dialogService,
                                           IPreferencesService preferencesService,
                                           IWinoServerConnectionManager winoServerConnectionManager) : base(dialogService)
        {
            PreferencesService = preferencesService;
            _winoServerConnectionManager = winoServerConnectionManager;

            // Load the app termination behavior options

            _appTerminationBehavior =
            [
                Translator.SettingsAppPreferences_ServerBackgroundingMode_MinimizeTray_Title, // "Minimize to tray"
                Translator.SettingsAppPreferences_ServerBackgroundingMode_Invisible_Title, // "Invisible"
                Translator.SettingsAppPreferences_ServerBackgroundingMode_Terminate_Title // "Terminate"
            ];

            SelectedAppTerminationBehavior = _appTerminationBehavior[(int)PreferencesService.ServerTerminationBehavior];
        }

        protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(SelectedAppTerminationBehavior))
            {
                var terminationModeChangedResult = await _winoServerConnectionManager.GetResponseAsync<bool, ServerTerminationModeChanged>(new ServerTerminationModeChanged(PreferencesService.ServerTerminationBehavior));

                if (!terminationModeChangedResult.IsSuccess)
                {
                    DialogService.InfoBarMessage(Translator.GeneralTitle_Error, terminationModeChangedResult.Message, InfoBarMessageType.Error);
                }
            }
        }
    }
}
