using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels
{
    public partial class AppPreferencesPageViewModel : BaseViewModel
    {
        public IPreferencesService PreferencesService { get; }

        [ObservableProperty]
        private List<string> _appTerminationBehavior;

        private string _selectedAppTerminationBehavior;

        public string SelectedsAppTerminationBehavior
        {
            get => _selectedAppTerminationBehavior;
            set
            {
                SetProperty(ref _selectedAppTerminationBehavior, value);

                PreferencesService.ServerTerminationBehavior = (ServerBackgroundMode)AppTerminationBehavior.IndexOf(value);
            }
        }

        public AppPreferencesPageViewModel(IDialogService dialogService, IPreferencesService preferencesService) : base(dialogService)
        {
            PreferencesService = preferencesService;

            // Load the app termination behavior options

            _appTerminationBehavior = new List<string>
            {
                Translator.SettingsAppPreferences_ServerBackgroundingMode_MinimizeTray_Title, // "Minimize to tray"
                Translator.SettingsAppPreferences_ServerBackgroundingMode_Invisible_Title, // "Invisible"
                Translator.SettingsAppPreferences_ServerBackgroundingMode_Terminate_Title // "Terminate"
            };

            SelectedsAppTerminationBehavior = _appTerminationBehavior[(int)PreferencesService.ServerTerminationBehavior];
        }
    }
}
