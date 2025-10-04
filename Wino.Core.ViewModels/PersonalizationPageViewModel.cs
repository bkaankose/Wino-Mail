using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels.Data;

namespace Wino.Core.ViewModels;

public partial class PersonalizationPageViewModel : CoreBaseViewModel
{
    public IStatePersistanceService StatePersistenceService { get; }
    public IPreferencesService PreferencesService { get; }

    private readonly IDialogServiceBase _dialogService;
    private readonly INewThemeService _newThemeService;

    private bool isPropChangeDisabled = false;

    // Sample mail copy to use in previewing mail display modes.
    public MailCopy DemoPreviewMailCopy { get; } = new MailCopy()
    {
        FromName = "Sender Name",
        Subject = "Mail Subject",
        PreviewText = "Thank you for using Wino Mail. We hope you enjoy the experience.",
    };

    #region Personalization

    public bool IsSelectedWindowsAccentColor => SelectedAppColor == Colors.LastOrDefault();

    public ObservableCollection<AppColorViewModel> Colors { get; set; } = [];

    public List<ElementThemeContainer> ElementThemes { get; set; } =
    [
        new ElementThemeContainer(ApplicationElementTheme.Light, Translator.ElementTheme_Light),
        new ElementThemeContainer(ApplicationElementTheme.Dark, Translator.ElementTheme_Dark),
        new ElementThemeContainer(ApplicationElementTheme.Default, Translator.ElementTheme_Default),
    ];

    public List<MailListDisplayMode> InformationDisplayModes { get; set; } =
    [
        MailListDisplayMode.Compact,
        MailListDisplayMode.Medium,
        MailListDisplayMode.Spacious
    ];

    public List<AppThemeBase> AppThemes { get; set; }

    [ObservableProperty]
    private ElementThemeContainer selectedElementTheme;

    [ObservableProperty]
    private MailListDisplayMode selectedInfoDisplayMode;

    private AppColorViewModel _selectedAppColor;

    public AppColorViewModel SelectedAppColor
    {
        get => _selectedAppColor;
        set
        {
            if (SetProperty(ref _selectedAppColor, value))
            {
                UseAccentColor = value == Colors?.LastOrDefault();
            }
        }
    }

    private bool _useAccentColor;
    public bool UseAccentColor
    {
        get => _useAccentColor;
        set
        {
            if (SetProperty(ref _useAccentColor, value))
            {
                if (value)
                {
                    SelectedAppColor = Colors?.LastOrDefault();
                }
                else if (SelectedAppColor == Colors?.LastOrDefault())
                {
                    // Unchecking from accent color.

                    SelectedAppColor = Colors?.FirstOrDefault();
                }
            }
        }
    }

    // Allow app theme change for system themes.
    public bool CanSelectElementTheme => SelectedAppTheme != null &&
        (SelectedAppTheme.AppThemeType == AppThemeType.System || SelectedAppTheme.AppThemeType == AppThemeType.Custom);

    private AppThemeBase _selectedAppTheme;

    public AppThemeBase SelectedAppTheme
    {
        get => _selectedAppTheme;
        set
        {
            if (SetProperty(ref _selectedAppTheme, value))
            {
                OnPropertyChanged(nameof(CanSelectElementTheme));

                if (!CanSelectElementTheme)
                {
                    SelectedElementTheme = null;
                }
            }
        }
    }

    // Backdrop selection properties
    [ObservableProperty]
    public partial List<BackdropTypeWrapper> AvailableBackdropTypes { get; set; }

    [ObservableProperty]
    public partial BackdropTypeWrapper SelectedBackdropType { get; set; }

    #endregion

    [RelayCommand]
    private void ResetMailListPaneLength()
    {
        StatePersistenceService.MailListPaneLength = 420;
        _dialogService.InfoBarMessage(Translator.GeneralTitle_Info, Translator.Info_MailListSizeResetSuccessMessage, InfoBarMessageType.Success);
    }

    public AsyncRelayCommand CreateCustomThemeCommand { get; set; }
    public PersonalizationPageViewModel(IDialogServiceBase dialogService,
                                        IStatePersistanceService statePersistanceService,
                                        INewThemeService newThemeService,
                                        IPreferencesService preferencesService)
    {
        _dialogService = dialogService;
        _newThemeService = newThemeService;

        StatePersistenceService = statePersistanceService;
        PreferencesService = preferencesService;

        CreateCustomThemeCommand = new AsyncRelayCommand(CreateCustomThemeAsync);
    }

    private async Task CreateCustomThemeAsync()
    {
        bool isThemeCreated = await _dialogService.ShowCustomThemeBuilderDialogAsync();

        if (isThemeCreated)
        {
            // Reload themes.

            await InitializeSettingsAsync();
        }
    }

    private void InitializeColors()
    {
        Colors.Add(new AppColorViewModel("#0078d7"));
        Colors.Add(new AppColorViewModel("#00838c"));
        Colors.Add(new AppColorViewModel("#e3008c"));
        Colors.Add(new AppColorViewModel("#ca4f07"));
        Colors.Add(new AppColorViewModel("#e81123"));
        Colors.Add(new AppColorViewModel("#00819e"));
        Colors.Add(new AppColorViewModel("#10893e"));
        Colors.Add(new AppColorViewModel("#881798"));
        Colors.Add(new AppColorViewModel("#c239b3"));
        Colors.Add(new AppColorViewModel("#767676"));
        Colors.Add(new AppColorViewModel("#e1b12c"));
        Colors.Add(new AppColorViewModel("#16a085"));
        Colors.Add(new AppColorViewModel("#0984e3"));
        Colors.Add(new AppColorViewModel("#4a69bd"));
        Colors.Add(new AppColorViewModel("#05c46b"));

        // Add system accent color as last item.

        Colors.Add(new AppColorViewModel(_newThemeService.GetSystemAccentColorHex(), true));
    }

    /// <summary>
    /// Set selections from settings service.
    /// </summary>
    private void SetInitialValues()
    {
        SelectedElementTheme = ElementThemes.Find(a => a.NativeTheme == _newThemeService.RootTheme);
        SelectedInfoDisplayMode = PreferencesService.MailItemDisplayMode;

        var currentAccentColor = _newThemeService.AccentColor;

        bool isWindowsColor = string.IsNullOrEmpty(currentAccentColor);

        if (isWindowsColor)
        {
            SelectedAppColor = Colors.LastOrDefault();
            UseAccentColor = true;
        }
        else
            SelectedAppColor = Colors.FirstOrDefault(a => a.Hex == currentAccentColor);

        // Find selected theme, handling backward compatibility where theme ID might not exist
        var currentThemeId = _newThemeService.CurrentApplicationThemeId;
        SelectedAppTheme = currentThemeId.HasValue ? AppThemes.Find(a => a.Id == currentThemeId.Value) : null;

        // Set the current backdrop from service - backdrop should be independent of theme selection
        var currentBackdropType = _newThemeService.CurrentBackdropType;
        SelectedBackdropType = AvailableBackdropTypes?.FirstOrDefault(x => x.BackdropType == currentBackdropType);
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        await InitializeSettingsAsync();
    }

    private async Task InitializeSettingsAsync()
    {
        Deactivate();

        AppThemes = await _newThemeService.GetAvailableThemesAsync();

        OnPropertyChanged(nameof(AppThemes));

        // Initialize backdrop types
        AvailableBackdropTypes = _newThemeService.GetAvailableBackdropTypes();

        InitializeColors();
        SetInitialValues();

        PropertyChanged -= PersonalizationSettingsUpdated;
        PropertyChanged += PersonalizationSettingsUpdated;

        _newThemeService.AccentColorChanged -= AccentColorChanged;
        _newThemeService.ElementThemeChanged -= ElementThemeChanged;

        _newThemeService.AccentColorChanged += AccentColorChanged;
        _newThemeService.ElementThemeChanged += ElementThemeChanged;
    }

    private void AccentColorChanged(object sender, string e)
    {
        isPropChangeDisabled = true;

        SelectedAppColor = Colors.FirstOrDefault(a => a.Hex == e);

        isPropChangeDisabled = false;
    }

    private void ElementThemeChanged(object sender, ApplicationElementTheme e)
    {
        isPropChangeDisabled = true;

        SelectedElementTheme = ElementThemes.Find(a => a.NativeTheme == e);

        isPropChangeDisabled = false;
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();

        Deactivate();
    }

    private void Deactivate()
    {
        PropertyChanged -= PersonalizationSettingsUpdated;

        _newThemeService.AccentColorChanged -= AccentColorChanged;
        _newThemeService.ElementThemeChanged -= ElementThemeChanged;

        if (AppThemes != null)
        {
            AppThemes.Clear();
            AppThemes = null;
        }
    }

    private void PersonalizationSettingsUpdated(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (isPropChangeDisabled)
            return;

        if (e.PropertyName == nameof(SelectedElementTheme) && SelectedElementTheme != null)
        {
            _newThemeService.RootTheme = SelectedElementTheme.NativeTheme;
        }
        else if (e.PropertyName == nameof(SelectedAppTheme))
        {
            // Set the theme ID, can be null if no theme is selected
            _newThemeService.CurrentApplicationThemeId = SelectedAppTheme?.Id;

            // Theme selection should not affect backdrop - they are independent settings
        }
        else if (e.PropertyName == nameof(SelectedBackdropType) && SelectedBackdropType != null)
        {
            _newThemeService.CurrentBackdropType = SelectedBackdropType.BackdropType;
        }
        else
        {
            if (e.PropertyName == nameof(SelectedInfoDisplayMode))
                PreferencesService.MailItemDisplayMode = SelectedInfoDisplayMode;
            else if (e.PropertyName == nameof(SelectedAppColor))
                _newThemeService.AccentColor = SelectedAppColor.Hex;
        }
    }
}
