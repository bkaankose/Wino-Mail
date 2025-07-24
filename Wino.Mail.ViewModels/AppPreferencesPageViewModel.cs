using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Server;

namespace Wino.Mail.ViewModels;

public partial class AppPreferencesPageViewModel : MailBaseViewModel
{
    public IPreferencesService PreferencesService { get; }

    [ObservableProperty]
    private List<string> _appTerminationBehavior;

    [ObservableProperty]
    public partial List<string> SearchModes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupBehaviorDisabled))]
    [NotifyPropertyChangedFor(nameof(IsStartupBehaviorEnabled))]
    private StartupBehaviorResult startupBehaviorResult;

    private int _emailSyncIntervalMinutes;
    public int EmailSyncIntervalMinutes
    {
        get => _emailSyncIntervalMinutes;
        set
        {
            SetProperty(ref _emailSyncIntervalMinutes, value);

            PreferencesService.EmailSyncIntervalMinutes = value;
        }
    }

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

    private string _selectedDefaultSearchMode;
    public string SelectedDefaultSearchMode
    {
        get => _selectedDefaultSearchMode;
        set
        {
            SetProperty(ref _selectedDefaultSearchMode, value);

            PreferencesService.DefaultSearchMode = (SearchMode)SearchModes.IndexOf(value);
        }
    }

    private readonly IMailDialogService _dialogService;
    private readonly IWinoServerConnectionManager _winoServerConnectionManager;
    private readonly IStartupBehaviorService _startupBehaviorService;

    public AppPreferencesPageViewModel(IMailDialogService dialogService,
                                       IPreferencesService preferencesService,
                                       IWinoServerConnectionManager winoServerConnectionManager,
                                       IStartupBehaviorService startupBehaviorService)
    {
        _dialogService = dialogService;
        PreferencesService = preferencesService;
        _winoServerConnectionManager = winoServerConnectionManager;
        _startupBehaviorService = startupBehaviorService;

        // Load the app termination behavior options

        _appTerminationBehavior =
        [
            Translator.SettingsAppPreferences_ServerBackgroundingMode_MinimizeTray_Title, // "Minimize to tray"
            Translator.SettingsAppPreferences_ServerBackgroundingMode_Invisible_Title, // "Invisible"
            Translator.SettingsAppPreferences_ServerBackgroundingMode_Terminate_Title // "Terminate"
        ];

        SearchModes =
        [
            Translator.SettingsAppPreferences_SearchMode_Local,
            Translator.SettingsAppPreferences_SearchMode_Online
        ];

        SelectedAppTerminationBehavior = _appTerminationBehavior[(int)PreferencesService.ServerTerminationBehavior];
        SelectedDefaultSearchMode = SearchModes[(int)PreferencesService.DefaultSearchMode];
        EmailSyncIntervalMinutes = PreferencesService.EmailSyncIntervalMinutes;
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
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsAppPreferences_StartupBehavior_Enabled, InfoBarMessageType.Success);
        }
        else if (StartupBehaviorResult == StartupBehaviorResult.Disabled)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsAppPreferences_StartupBehavior_Disabled, InfoBarMessageType.Warning);
        }
        else if (StartupBehaviorResult == StartupBehaviorResult.DisabledByPolicy)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsAppPreferences_StartupBehavior_DisabledByPolicy, InfoBarMessageType.Warning);
        }
        else if (StartupBehaviorResult == StartupBehaviorResult.DisabledByUser)
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.SettingsAppPreferences_StartupBehavior_DisabledByUser, InfoBarMessageType.Warning);
        }
        else
        {
            _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, Translator.SettingsAppPreferences_StartupBehavior_FatalError, InfoBarMessageType.Error);
        }
    }

    protected override async void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        if (e.PropertyName == nameof(SelectedAppTerminationBehavior))
        {
            var terminationModeChangedResult = await _winoServerConnectionManager.GetResponseAsync<bool, ServerTerminationModeChanged>(new ServerTerminationModeChanged(PreferencesService.ServerTerminationBehavior));

            if (!terminationModeChangedResult.IsSuccess)
            {
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, terminationModeChangedResult.Message, InfoBarMessageType.Error);
            }
        }
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        StartupBehaviorResult = await _startupBehaviorService.GetCurrentStartupBehaviorAsync();
    }
}
