using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Translations;

namespace Wino.Mail.ViewModels;

public partial class AppPreferencesPageViewModel : MailBaseViewModel
{
    public AppPreferencesPageViewModel(
        IMailDialogService dialogService,
        IPreferencesService preferencesService,
        IStartupBehaviorService startupBehaviorService,
        ITranslationService translationService,
        IAiActionOptionsService aiActionOptionsService)
    {
        _dialogService = dialogService;
        PreferencesService = preferencesService;
        _startupBehaviorService = startupBehaviorService;
        _translationService = translationService;
        _aiActionOptionsService = aiActionOptionsService;

        SearchModes =
        [
            Translator.SettingsAppPreferences_SearchMode_Local,
            Translator.SettingsAppPreferences_SearchMode_Online
        ];

        ApplicationModes =
        [
            Translator.SettingsAppPreferences_ApplicationMode_Mail,
            Translator.SettingsAppPreferences_ApplicationMode_Calendar,
            Translator.ContactsPage_Title,
            Translator.MenuSettings
        ];

        SelectedDefaultSearchMode = SearchModes[(int)PreferencesService.DefaultSearchMode];
        SelectedDefaultApplicationMode = ApplicationModes[(int)PreferencesService.DefaultApplicationMode];
        EmailSyncIntervalMinutes = PreferencesService.EmailSyncIntervalMinutes;
        SummarySavePath = PreferencesService.AiSummarySavePath;
    }

    public IPreferencesService PreferencesService { get; }

    [ObservableProperty]
    public partial List<string> SearchModes { get; set; }

    [ObservableProperty]
    public partial List<string> ApplicationModes { get; set; }

    [ObservableProperty]
    public partial List<AppLanguageModel> AvailableLanguages { get; set; } = [];

    [ObservableProperty]
    public partial AppLanguageModel SelectedLanguage { get; set; }

    [ObservableProperty]
    public partial List<AiTranslateLanguageOption> AvailableAiLanguages { get; set; } = [];

    [ObservableProperty]
    public partial AiTranslateLanguageOption SelectedDefaultTranslationLanguage { get; set; }

    [ObservableProperty]
    public partial AiTranslateLanguageOption SelectedSummarizeLanguage { get; set; }

    [ObservableProperty]
    public partial string SummarySavePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasInvalidSummarySavePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStartupBehaviorDisabled))]
    [NotifyPropertyChangedFor(nameof(IsStartupBehaviorEnabled))]
    private StartupBehaviorResult startupBehaviorResult;

    private readonly IMailDialogService _dialogService;
    private readonly IStartupBehaviorService _startupBehaviorService;
    private readonly ITranslationService _translationService;
    private readonly IAiActionOptionsService _aiActionOptionsService;
    private bool _isLanguageInitialized;
    private bool _isAiPreferencesInitialized;
    private int _emailSyncIntervalMinutes;
    private string _selectedDefaultSearchMode;
    private string _selectedDefaultApplicationMode;

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

    public string SelectedDefaultSearchMode
    {
        get => _selectedDefaultSearchMode;
        set
        {
            SetProperty(ref _selectedDefaultSearchMode, value);
            PreferencesService.DefaultSearchMode = (SearchMode)SearchModes.IndexOf(value);
        }
    }

    public string SelectedDefaultApplicationMode
    {
        get => _selectedDefaultApplicationMode;
        set
        {
            SetProperty(ref _selectedDefaultApplicationMode, value);
            PreferencesService.DefaultApplicationMode = (WinoApplicationMode)ApplicationModes.IndexOf(value);
        }
    }

    partial void OnSelectedLanguageChanged(AppLanguageModel value)
    {
        if (!_isLanguageInitialized || value == null)
            return;

        _ = _translationService.InitializeLanguageAsync(value.Language);
    }

    partial void OnSelectedDefaultTranslationLanguageChanged(AiTranslateLanguageOption value)
    {
        if (!_isAiPreferencesInitialized || value == null)
            return;

        PreferencesService.AiDefaultTranslationLanguageCode = value.Code;
    }

    partial void OnSelectedSummarizeLanguageChanged(AiTranslateLanguageOption value)
    {
        if (!_isAiPreferencesInitialized || value == null)
            return;

        PreferencesService.AiSummarizeLanguageCode = value.Code;
    }

    partial void OnSummarySavePathChanged(string value)
    {
        if (!_isAiPreferencesInitialized)
            return;

        PreferencesService.AiSummarySavePath = value ?? string.Empty;
        RefreshSummarySavePathState();
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

    [RelayCommand]
    private async Task BrowseSummarySavePathAsync()
    {
        var pickedPath = await _dialogService.PickWindowsFolderAsync();
        if (string.IsNullOrWhiteSpace(pickedPath))
            return;

        await ExecuteUIThread(() => SummarySavePath = pickedPath);
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        var availableLanguages = _translationService.GetAvailableLanguages();
        var availableAiLanguages = new List<AiTranslateLanguageOption>(_aiActionOptionsService.GetTranslateLanguageOptions());
        var startupBehaviorResult = await _startupBehaviorService.GetCurrentStartupBehaviorAsync();

        await ExecuteUIThread(() =>
        {
            AvailableLanguages = availableLanguages;
            SelectedLanguage = AvailableLanguages.Find(language => language.Language == PreferencesService.CurrentLanguage)
                               ?? (AvailableLanguages.Count > 0 ? AvailableLanguages[0] : null);
            _isLanguageInitialized = true;

            AvailableAiLanguages = availableAiLanguages;
            SelectedDefaultTranslationLanguage = FindAiLanguageOption(PreferencesService.AiDefaultTranslationLanguageCode)
                                                 ?? FindAiLanguageOption("en-US")
                                                 ?? (AvailableAiLanguages.Count > 0 ? AvailableAiLanguages[0] : null);
            SelectedSummarizeLanguage = FindAiLanguageOption(PreferencesService.AiSummarizeLanguageCode)
                                        ?? FindAiLanguageOption("en-US")
                                        ?? (AvailableAiLanguages.Count > 0 ? AvailableAiLanguages[0] : null);
            SummarySavePath = PreferencesService.AiSummarySavePath;
            RefreshSummarySavePathState();
            _isAiPreferencesInitialized = true;

            StartupBehaviorResult = startupBehaviorResult;
        });
    }

    private AiTranslateLanguageOption FindAiLanguageOption(string languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
            return null;

        return AvailableAiLanguages.Find(option => option.Code == languageCode);
    }

    private void RefreshSummarySavePathState()
    {
        HasInvalidSummarySavePath = !string.IsNullOrWhiteSpace(SummarySavePath) && !Directory.Exists(SummarySavePath);
    }
}
