using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;

namespace Wino.Mail.ViewModels;

/// <summary>
/// App-wide settings that belong to no single mode: display language, how Wino starts, and what
/// happens when the window is closed. Mail behavior lives on <see cref="MailPreferencesPageViewModel"/>
/// and the AI action languages live with Wino Intelligence.
/// </summary>
public partial class AppPreferencesPageViewModel : MailBaseViewModel
{
    private readonly IMailDialogService _dialogService;
    private readonly IStartupBehaviorService _startupBehaviorService;
    private readonly ITranslationService _translationService;

    private bool _isLanguageInitialized;
    private string _selectedCloseBehaviorMode;

    public AppPreferencesPageViewModel(
        IMailDialogService dialogService,
        IPreferencesService preferencesService,
        IStartupBehaviorService startupBehaviorService,
        ITranslationService translationService)
    {
        _dialogService = dialogService;
        PreferencesService = preferencesService;
        _startupBehaviorService = startupBehaviorService;
        _translationService = translationService;

        CloseBehaviorModes =
        [
            Translator.SettingsAppPreferences_ServerBackgroundingMode_MinimizeTray_Title,
            Translator.SettingsAppPreferences_ServerBackgroundingMode_Invisible_Title,
            Translator.SettingsAppPreferences_ServerBackgroundingMode_Terminate_Title
        ];

        _selectedCloseBehaviorMode = CloseBehaviorModes[(int)PreferencesService.AppCloseBehavior];
    }

    public IPreferencesService PreferencesService { get; }

    [ObservableProperty]
    public partial List<string> CloseBehaviorModes { get; set; }

    [ObservableProperty]
    public partial List<AppLanguageModel> AvailableLanguages { get; set; } = [];

    [ObservableProperty]
    public partial AppLanguageModel SelectedLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupBehaviorDisabled))]
    [NotifyPropertyChangedFor(nameof(IsStartupBehaviorEnabled))]
    public partial StartupBehaviorResult StartupBehaviorResult { get; set; }

    public bool IsStartupBehaviorDisabled => !IsStartupBehaviorEnabled;
    public bool IsStartupBehaviorEnabled => StartupBehaviorResult == StartupBehaviorResult.Enabled;

    public string SelectedCloseBehaviorMode
    {
        get => _selectedCloseBehaviorMode;
        set
        {
            if (!SetProperty(ref _selectedCloseBehaviorMode, value))
                return;

            var selectedIndex = CloseBehaviorModes.IndexOf(value);

            if (selectedIndex >= 0)
            {
                PreferencesService.AppCloseBehavior = (AppCloseBehavior)selectedIndex;
            }
        }
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        var availableLanguages = _translationService.GetAvailableLanguages();
        var startupBehaviorResult = await _startupBehaviorService.GetCurrentStartupBehaviorAsync();

        await ExecuteUIThread(() =>
        {
            AvailableLanguages = availableLanguages;
            SelectedLanguage = AvailableLanguages.Find(language => language.Language == PreferencesService.CurrentLanguage)
                               ?? (AvailableLanguages.Count > 0 ? AvailableLanguages[0] : null);
            _isLanguageInitialized = true;

            StartupBehaviorResult = startupBehaviorResult;
        });
    }

    partial void OnSelectedLanguageChanged(AppLanguageModel value)
    {
        if (!_isLanguageInitialized || value == null)
            return;

        _ = _translationService.InitializeLanguageAsync(value.Language);
    }

    [RelayCommand]
    private async Task ToggleStartupBehaviorAsync()
    {
        if (IsStartupBehaviorEnabled)
            await DisableStartupAsync();
        else
            await EnableStartupAsync();

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
}
