using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
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

    private bool isPropChangeDisabled = false;

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
                                        IThumbnailService thumbnailService)
    {
        _dialogService = dialogService;
        _newThemeService = newThemeService;
        _thumbnailService = thumbnailService;

        StatePersistenceService = statePersistanceService;
        PreferencesService = preferencesService;

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

        var themes = await _newThemeService.GetAvailableThemesAsync();
        var currentTheme = _newThemeService.CurrentApplicationThemeId is Guid currentThemeId
            ? themes.FirstOrDefault(theme => theme.Id == currentThemeId)
            : themes.FirstOrDefault();
        CanSelectElementTheme = currentTheme?.AppThemeType is AppThemeType.System or AppThemeType.Custom;
        OnPropertyChanged(nameof(CanSelectElementTheme));

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

    private sealed class DemoMailItemDisplayInformation(
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
        public IReadOnlyList<MailCategory> Categories { get; } =
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
