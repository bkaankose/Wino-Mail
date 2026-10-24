using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public partial class PersonalizationPageViewModel : CoreBaseViewModel
{
    public IStatePersistanceService StatePersistenceService { get; }
    public IPreferencesService PreferencesService { get; }

    private readonly IDialogServiceBase _dialogService;
    private readonly INewThemeService _newThemeService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IAppModeReadinessService _appModeReadinessService;

    private bool isPropChangeDisabled = false;
    private int _appModeRequestId;

    public ObservableCollection<AppModeOptionViewModel> AppModeOptions { get; } = [];

    // Sample mail copy to use in previewing mail display modes.
    public MailCopy DemoPreviewMailCopy { get; } = new MailCopy()
    {
        FromName = "Sender Name",
        FromAddress = "sender@wino.mail",
        Subject = "Mail Subject",
        PreviewText = "Thank you for using Wino Mail. We hope you enjoy the experience.",
    };

    public IMailItemDisplayInformation DemoPreviewMailItemInformation { get; }

    #region Personalization

    public bool IsSelectedWindowsAccentColor => SelectedAppColor == Colors.LastOrDefault();

    public ObservableCollection<AppColorViewModel> Colors { get; set; } = [];

    public List<ElementThemeContainer> ElementThemes { get; set; } =
    [
        new ElementThemeContainer(ApplicationElementTheme.Default, Translator.ElementTheme_Default),
        new ElementThemeContainer(ApplicationElementTheme.Light, Translator.ElementTheme_Light),
        new ElementThemeContainer(ApplicationElementTheme.Dark, Translator.ElementTheme_Dark),
    ];

    [ObservableProperty]
    public partial ElementThemeContainer SelectedElementTheme { get; set; }

    public List<IconStyleContainer> IconStyles { get; } =
    [
        new IconStyleContainer(WinoIconStyle.Monochrome, Translator.IconStyle_Monochrome),
        new IconStyleContainer(WinoIconStyle.Colorful, Translator.IconStyle_Colorful),
    ];

    [ObservableProperty]
    public partial IconStyleContainer SelectedIconStyle { get; set; }

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

    // Themes with both palettes can follow the requested element theme.
    public bool CanSelectElementTheme { get; private set; } = true;

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

    public PersonalizationPageViewModel(IDialogServiceBase dialogService,
                                        IStatePersistanceService statePersistanceService,
                                        INewThemeService newThemeService,
                                        IPreferencesService preferencesService,
                                        IThumbnailService thumbnailService,
                                        IAppModeReadinessService appModeReadinessService)
    {
        _dialogService = dialogService;
        _newThemeService = newThemeService;
        _thumbnailService = thumbnailService;
        _appModeReadinessService = appModeReadinessService;

        StatePersistenceService = statePersistanceService;
        PreferencesService = preferencesService;

        foreach (var mode in AppModeVisibility.SwitchableModes)
        {
            AppModeOptions.Add(new AppModeOptionViewModel(mode, PreferencesService.SetApplicationModeHidden));
        }

        DemoPreviewMailItemInformation = new DemoMailItemDisplayInformation(
            DemoPreviewMailCopy.FromName,
            DemoPreviewMailCopy.FromAddress,
            DemoPreviewMailCopy.Subject,
            DemoPreviewMailCopy.PreviewText);

    }

    [RelayCommand]
    private void NavigateApplicationThemes()
    {
        WeakReferenceMessenger.Default.Send(new BreadcrumbNavigationRequested(
            Translator.ApplicationThemeGallery_Title,
            WinoPage.ApplicationThemeGalleryPage));
    }

    private void InitializeColors()
    {
        Colors.Clear();
        foreach (var hex in AppearanceSettingsModel.AccentPalette)
            Colors.Add(new AppColorViewModel(hex));

        // Add system accent color as last item.

        Colors.Add(new AppColorViewModel(_newThemeService.GetSystemAccentColorHex(), true));
    }

    /// <summary>
    /// Set selections from settings service.
    /// </summary>
    private void SetInitialValues()
    {
        SelectedElementTheme = ElementThemes.Find(a => a.NativeTheme == _newThemeService.RootTheme)
            ?? ElementThemes.FirstOrDefault();

        var currentAccentColor = _newThemeService.AccentColor;

        bool isWindowsColor = string.IsNullOrEmpty(currentAccentColor);

        if (isWindowsColor)
        {
            SelectedAppColor = Colors.LastOrDefault();
            UseAccentColor = true;
        }
        else
            SelectedAppColor = Colors.FirstOrDefault(a => a.Hex == currentAccentColor);

        SelectedIconStyle = IconStyles.Find(a => a.IconStyle == PreferencesService.IconStyle) ?? IconStyles[0];

        // Set the current backdrop from service - backdrop should be independent of theme selection
        var currentBackdropType = _newThemeService.CurrentBackdropType;
        SelectedBackdropType = AvailableBackdropTypes?.FirstOrDefault(x => x.BackdropType == currentBackdropType);
    }

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
        => await InitializeNavigationAsync(mode, parameters);

    public async Task InitializeNavigationAsync(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        await InitializeSettingsAsync();
    }

    public async Task InitializeSettingsAsync()
    {
        Deactivate();

        var themes = await _newThemeService.GetAvailableThemesAsync();
        var currentTheme = _newThemeService.CurrentApplicationThemeId is Guid currentThemeId
            ? themes.FirstOrDefault(theme => theme.Id == currentThemeId)
            : themes.FirstOrDefault();
        CanSelectElementTheme = currentTheme?.Compatibility == ThemeCompatibility.Both;
        OnPropertyChanged(nameof(CanSelectElementTheme));

        // Initialize backdrop types
        AvailableBackdropTypes = _newThemeService.GetAvailableBackdropTypes();

        InitializeColors();
        SetInitialValues();
        await ActivateAppModeOptionsAsync();

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

    /// <summary>
    /// Fills <see cref="AppModeOptions"/> and follows account changes until <see cref="DeactivateAppModeOptions"/>.
    /// The native pages that lay the Windows pickers out themselves (and skip OnNavigatedTo) call this directly.
    /// </summary>
    public async Task ActivateAppModeOptionsAsync()
    {
        _appModeReadinessService.ReadinessInvalidated -= AppModeReadinessInvalidated;
        _appModeReadinessService.ReadinessInvalidated += AppModeReadinessInvalidated;
        await RefreshAppModeOptionsAsync();
    }

    public void DeactivateAppModeOptions() => _appModeReadinessService.ReadinessInvalidated -= AppModeReadinessInvalidated;

    private void AppModeReadinessInvalidated(object sender, EventArgs e) => _ = RefreshAppModeOptionsAsync();

    private async Task RefreshAppModeOptionsAsync()
    {
        var requestId = Interlocked.Increment(ref _appModeRequestId);
        var visibility = await _appModeReadinessService.GetVisibilityAsync();

        if (requestId != _appModeRequestId)
            return;

        await ExecuteUIThreadAsync(() =>
        {
            if (requestId != _appModeRequestId)
                return Task.CompletedTask;

            foreach (var option in AppModeOptions)
            {
                option.Apply(visibility);
            }

            return Task.CompletedTask;
        });
    }

    private void Deactivate()
    {
        PropertyChanged -= PersonalizationSettingsUpdated;
        DeactivateAppModeOptions();

        _newThemeService.AccentColorChanged -= AccentColorChanged;
        _newThemeService.ElementThemeChanged -= ElementThemeChanged;

    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        Deactivate();
        base.OnNavigatedFrom(mode, parameters);
    }

    private void PersonalizationSettingsUpdated(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (isPropChangeDisabled)
            return;

        if (e.PropertyName == nameof(SelectedElementTheme) && SelectedElementTheme != null)
        {
            _newThemeService.RootTheme = SelectedElementTheme.NativeTheme;
        }
        else if (e.PropertyName == nameof(SelectedBackdropType) && SelectedBackdropType != null)
        {
            _newThemeService.CurrentBackdropType = SelectedBackdropType.BackdropType;
        }
        else if (e.PropertyName == nameof(SelectedIconStyle) && SelectedIconStyle != null)
        {
            PreferencesService.IconStyle = SelectedIconStyle.IconStyle;
        }
        else
        {
            if (e.PropertyName == nameof(SelectedAppColor))
                _newThemeService.AccentColor = SelectedAppColor.Hex;
        }
    }

    /// <summary>
    /// Drops the cached Gravatar and favicon images so the next render fetches them again.
    /// </summary>
    [RelayCommand]
    private async Task ClearAvatarsCacheAsync() => await _thumbnailService.ClearCache();

    private sealed partial class DemoMailItemDisplayInformation(
        string fromName,
        string fromAddress,
        string subject,
        string previewText) : IMailItemDisplayInformation
    {
        public string Subject { get; } = subject;
        public string FromName { get; } = fromName;
        public string FromAddress { get; } = fromAddress;
        public string PreviewText { get; } = previewText;
        public bool IsRead { get; } = false;
        public bool IsDraft { get; } = false;
        public bool IsLocalDraft { get; } = false;
        public bool IsDraftSyncFailed { get; } = false;
        public bool ShouldShowDraftSyncWarning { get; } = false;
        public string DraftSyncTooltip { get; } = string.Empty;
        public bool HasAttachments { get; } = false;
        public bool IsCalendarEvent { get; } = false;
        public bool IsFlagged { get; } = false;
        public DateTime CreationDate { get; } = DateTime.Now;
        public Guid? ContactPictureFileId { get; } = null;
        public bool ThumbnailUpdatedEvent { get; } = false;
        public bool IsBusy { get; } = false;
        public bool IsThreadExpanded { get; } = false;
        public bool HasReadReceiptTracking { get; } = false;
        public bool IsReadReceiptAcknowledged { get; } = false;
        public string ReadReceiptDisplayText { get; } = string.Empty;
        public string AccountNickname { get; } = "Personal";
        public string AccountColorHex { get; } = "#00FF00";
        public AccountNicknamePosition AccountNicknamePosition { get; } = Wino.Core.Domain.Enums.AccountNicknamePosition.Right;
        public ObservableCollection<MailCategory> Categories { get; } =
        [
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Follow Up",
                BackgroundColorHex = "#DCEBFF",
                TextColorHex = "#0B5CAD"
            },
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Planning",
                BackgroundColorHex = "#DDF5D7",
                TextColorHex = "#236A1E"
            }
        ];
        public bool HasCategories => Categories.Count > 0;
        public AccountContact SenderContact { get; } = null;
        event PropertyChangedEventHandler INotifyPropertyChanged.PropertyChanged
        {
            add { }
            remove { }
        }
    }
}
