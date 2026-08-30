using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Personalization;
using Wino.Core.ViewModels.Data;
using Wino.Messaging.Client.Navigation;

namespace Wino.Core.ViewModels;

public partial class ApplicationThemeEditorPageViewModel : CoreBaseViewModel,
    IConfirmBackNavigation,
    IBreadcrumbNavigationResultProvider
{
    private readonly INewThemeService _themeService;
    private readonly IDialogServiceBase _dialogService;
    private ThemeRuntimeState? _originalRuntimeState;
    private Guid? _themeId;
    private byte[]? _wallpaperData;
    private bool _wallpaperPreviewPrepared;
    private bool _isInitializing;
    private bool _isNavigationCommitted;
    private NavigationResult? _pendingNavigationResult;

    private CustomThemePalette _lightPalette = new();
    private CustomThemePalette _darkPalette = new();

    public ObservableCollection<ThemePaletteColorOptionViewModel> AdvancedColorOptions { get; } = [];
    public ObservableCollection<ThemeWallpaperAlignment> WallpaperAlignments { get; } =
        new(Enum.GetValues<ThemeWallpaperAlignment>());

    [ObservableProperty] public partial string ThemeName { get; set; } = string.Empty;
    [ObservableProperty] public partial string WallpaperPreviewPath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool UseSystemAccent { get; set; } = true;
    [ObservableProperty] public partial string AccentColorHex { get; set; } = string.Empty;
    [ObservableProperty] public partial ThemeWallpaperFit WallpaperFit { get; set; } = ThemeWallpaperFit.Fill;
    [ObservableProperty] public partial ThemeWallpaperAlignment WallpaperAlignment { get; set; } = ThemeWallpaperAlignment.Center;
    [ObservableProperty] public partial bool IsDarkPalette { get; set; }
    [ObservableProperty] public partial string BaseSurfaceColor { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsBaseSurfaceOverridden { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] public partial bool IsSaving { get; set; }
    [ObservableProperty] public partial bool IsErrorOpen { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;

    public bool IsEditMode => _themeId.HasValue;
    public bool IsFocalSelectorEnabled => WallpaperFit == ThemeWallpaperFit.Fill;
    public string PageTitle => IsEditMode ? Translator.ApplicationThemeEditor_EditTitle : Translator.ApplicationThemeEditor_CreateTitle;
    public int PaletteModeIndex { get => IsDarkPalette ? 1 : 0; set => IsDarkPalette = value == 1; }
    public int WallpaperFitIndex { get => WallpaperFit == ThemeWallpaperFit.Fill ? 0 : 1; set => WallpaperFit = value == 1 ? ThemeWallpaperFit.Fit : ThemeWallpaperFit.Fill; }

    public ApplicationThemeEditorPageViewModel(INewThemeService themeService, IDialogServiceBase dialogService)
    {
        _themeService = themeService;
        _dialogService = dialogService;
    }

    public override async void OnNavigatedTo(NavigationMode mode, object parameters)
    {
        base.OnNavigatedTo(mode, parameters);
        _isInitializing = true;
        _isNavigationCommitted = false;
        _pendingNavigationResult = null;
        _wallpaperData = null;
        _wallpaperPreviewPrepared = false;
        _originalRuntimeState = _themeService.CaptureRuntimeState();
        _themeId = (parameters as CustomThemeEditorNavigationParameter)?.ThemeId;

        try
        {
            if (_themeId is Guid themeId)
            {
                var metadata = await _themeService.GetCustomThemeAsync(themeId)
                               ?? throw new InvalidOperationException(Translator.SettingsCustomTheme_DeleteMissing);
                ThemeName = metadata.Name;
                UseSystemAccent = !metadata.HasCustomAccentColor;
                AccentColorHex = metadata.AccentColorHex;
                WallpaperPreviewPath = $"ms-appdata:///local/CustomThemes/{themeId}.jpg";
                WallpaperFit = metadata.WallpaperFit;
                WallpaperAlignment = metadata.WallpaperAlignment;
                _lightPalette = metadata.LightPalette?.Clone() ?? new CustomThemePalette();
                _darkPalette = metadata.DarkPalette?.Clone() ?? new CustomThemePalette();
            }
            else
            {
                ThemeName = string.Empty;
                UseSystemAccent = true;
                AccentColorHex = string.Empty;
                WallpaperPreviewPath = string.Empty;
                WallpaperFit = ThemeWallpaperFit.Fill;
                WallpaperAlignment = ThemeWallpaperAlignment.Center;
                _lightPalette = new CustomThemePalette();
                _darkPalette = new CustomThemePalette();
            }

            IsDarkPalette = _originalRuntimeState.ElementTheme == ApplicationElementTheme.Dark;
            RebuildPaletteOptions();
            IsDirty = false;
            IsErrorOpen = false;
            OnPropertyChanged(nameof(IsEditMode));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PaletteModeIndex));
            OnPropertyChanged(nameof(WallpaperFitIndex));
            OnPropertyChanged(nameof(IsFocalSelectorEnabled));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    [RelayCommand]
    private async Task ChooseWallpaperAsync()
    {
        var files = await _dialogService.PickFilesAsync(".jpg", ".jpeg", ".png");
        var file = files?.Count > 0 ? files[0] : null;

        if (file == null)
            return;

        _wallpaperData = file.Data;
        _wallpaperPreviewPrepared = false;
        WallpaperPreviewPath = new Uri(file.FullFilePath).AbsoluteUri;
        MarkDirtyAndPreview();
    }

    [RelayCommand]
    private void ResetBaseSurface()
    {
        CurrentPalette.ResetOverride(CustomThemeColorKey.BaseSurface);
        RebuildPaletteOptions();
        MarkDirtyAndPreview();
    }

    [RelayCommand]
    private void ResetColor(ThemePaletteColorOptionViewModel? option)
    {
        if (option == null)
            return;

        CurrentPalette.ResetOverride(option.Key);
        RebuildPaletteOptions();
        MarkDirtyAndPreview();
    }

    [RelayCommand]
    private void SelectFocalPoint(ThemeWallpaperAlignment alignment)
    {
        if (WallpaperFit != ThemeWallpaperFit.Fill)
            return;

        WallpaperAlignment = alignment;
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (!await CanNavigateBackAsync())
            return;

        WeakReferenceMessenger.Default.Send(new BackBreadcrumNavigationRequested(Result: TakeNavigationResult()));
    }

    private bool CanSave() => !IsSaving;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsSaving = true;
        IsErrorOpen = false;

        try
        {
            var request = new CustomThemeSaveRequest(
                _themeId,
                ThemeName,
                UseSystemAccent ? string.Empty : AccentColorHex,
                _wallpaperData,
                _lightPalette,
                _darkPalette,
                WallpaperFit,
                WallpaperAlignment);
            var savedTheme = await _themeService.SaveCustomThemeAsync(request);

            if (_originalRuntimeState != null)
                await _themeService.RestoreRuntimeStateAsync(_originalRuntimeState);

            await _themeService.SelectThemeAsync(savedTheme.Id, forceReapply: true);
            _isNavigationCommitted = true;
            IsDirty = false;
            _pendingNavigationResult = NavigationResult.Saved(savedTheme.Id);
            WeakReferenceMessenger.Default.Send(new BackBreadcrumNavigationRequested(Result: TakeNavigationResult()));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
        }
        finally
        {
            IsSaving = false;
        }
    }

    public async ValueTask<bool> CanNavigateBackAsync()
    {
        if (_isNavigationCommitted)
            return true;

        if (IsDirty)
        {
            var discard = await _dialogService.ShowConfirmationDialogAsync(
                Translator.ApplicationThemeEditor_DiscardMessage,
                Translator.ApplicationThemeEditor_DiscardTitle,
                Translator.ApplicationThemeEditor_DiscardAction);

            if (!discard)
                return false;
        }

        if (_originalRuntimeState != null)
            await _themeService.RestoreRuntimeStateAsync(_originalRuntimeState);

        _isNavigationCommitted = true;
        _pendingNavigationResult = NavigationResult.Cancelled();
        return true;
    }

    public NavigationResult? TakeNavigationResult()
    {
        var result = _pendingNavigationResult;
        _pendingNavigationResult = null;
        return result;
    }

    partial void OnThemeNameChanged(string value) => MarkDirty();
    partial void OnUseSystemAccentChanged(bool value) => MarkDirtyAndPreview();
    partial void OnAccentColorHexChanged(string value) => MarkDirtyAndPreview();
    partial void OnWallpaperAlignmentChanged(ThemeWallpaperAlignment value) => MarkDirtyAndPreview();

    partial void OnWallpaperFitChanged(ThemeWallpaperFit value)
    {
        if (value == ThemeWallpaperFit.Fit)
            WallpaperAlignment = ThemeWallpaperAlignment.Center;

        OnPropertyChanged(nameof(WallpaperFitIndex));
        OnPropertyChanged(nameof(IsFocalSelectorEnabled));
        MarkDirtyAndPreview();
    }

    partial void OnIsDarkPaletteChanged(bool value)
    {
        OnPropertyChanged(nameof(PaletteModeIndex));
        RebuildPaletteOptions();

        if (!_isInitializing)
            _ = PreviewAsync();
    }

    partial void OnBaseSurfaceColorChanged(string value)
    {
        if (_isInitializing)
            return;

        CurrentPalette.SetOverride(CustomThemeColorKey.BaseSurface, value);
        IsBaseSurfaceOverridden = true;
        MarkDirtyAndPreview();
    }

    private CustomThemePalette CurrentPalette => IsDarkPalette ? _darkPalette : _lightPalette;

    private void RebuildPaletteOptions()
    {
        var wasInitializing = _isInitializing;
        _isInitializing = true;
        var resolved = CurrentPalette.Resolve(IsDarkPalette);
        BaseSurfaceColor = resolved.MainCustomThemeColor ?? string.Empty;
        IsBaseSurfaceOverridden = !string.IsNullOrWhiteSpace(CurrentPalette.MainCustomThemeColor);
        AdvancedColorOptions.Clear();
        AddOption(CustomThemeColorKey.MailListHeader, Translator.ApplicationThemeEditor_MailHeader, Translator.ApplicationThemeEditor_GroupMail, resolved.MailListHeaderBackgroundColor);
        AddOption(CustomThemeColorKey.Workspace, Translator.ApplicationThemeEditor_WorkspaceSurface, Translator.ApplicationThemeEditor_GroupWorkspace, resolved.WinoContentZoneBackgroud);
        AddOption(CustomThemeColorKey.Navigation, Translator.ApplicationThemeEditor_NavigationSurface, Translator.ApplicationThemeEditor_GroupNavigation, resolved.NavigationViewContentBackground);
        AddOption(CustomThemeColorKey.ReadingPane, Translator.ApplicationThemeEditor_ReadingSurface, Translator.ApplicationThemeEditor_GroupReading, resolved.ReadingPaneBackgroundColorBrush);
        AddOption(CustomThemeColorKey.CalendarDefaultHour, Translator.ApplicationThemeEditor_CalendarDefault, Translator.ApplicationThemeEditor_GroupCalendar, resolved.CalendarDefaultHourBackgroundBrush);
        AddOption(CustomThemeColorKey.CalendarHoverHour, Translator.ApplicationThemeEditor_CalendarHover, Translator.ApplicationThemeEditor_GroupCalendar, resolved.CalendarHoverHourBackgroundBrush);
        AddOption(CustomThemeColorKey.CalendarWorkHour, Translator.ApplicationThemeEditor_CalendarWork, Translator.ApplicationThemeEditor_GroupCalendar, resolved.CalendarWorkHourBackgroundBrush);
        AddOption(CustomThemeColorKey.CalendarSelectedHour, Translator.ApplicationThemeEditor_CalendarSelected, Translator.ApplicationThemeEditor_GroupCalendar, resolved.CalendarSelectedHourBackgroundBrush);
        _isInitializing = wasInitializing;
    }

    private void AddOption(CustomThemeColorKey key, string label, string group, string? resolvedValue)
    {
        var option = new ThemePaletteColorOptionViewModel(
            key,
            label,
            group,
            resolvedValue ?? string.Empty,
            !string.IsNullOrWhiteSpace(CurrentPalette.GetOverride(key)));
        option.PropertyChanged += PaletteOptionChanged;
        AdvancedColorOptions.Add(option);
    }

    private void PaletteOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || sender is not ThemePaletteColorOptionViewModel option || e.PropertyName != nameof(option.Value))
            return;

        CurrentPalette.SetOverride(option.Key, option.Value);
        option.IsOverridden = true;
        MarkDirtyAndPreview();
    }

    private void MarkDirty()
    {
        if (!_isInitializing)
            IsDirty = true;
    }

    private void MarkDirtyAndPreview()
    {
        MarkDirty();

        if (!_isInitializing)
            _ = PreviewAsync();
    }

    private async Task PreviewAsync()
    {
        if (_isInitializing || (string.IsNullOrWhiteSpace(WallpaperPreviewPath) && _wallpaperData == null))
            return;

        var accent = string.Empty;
        if (!UseSystemAccent && !ThemeColorValidator.TryNormalizeOpaque(AccentColorHex, out accent))
            return;

        if (!ThemeColorValidator.IsValid(_lightPalette) || !ThemeColorValidator.IsValid(_darkPalette))
            return;

        try
        {
            var metadata = new CustomThemeMetadata
            {
                Id = _wallpaperData != null ? Guid.Empty : _themeId ?? Guid.Empty,
                Name = ThemeName,
                AccentColorHex = accent,
                LightPalette = _lightPalette,
                DarkPalette = _darkPalette,
                WallpaperFit = WallpaperFit,
                WallpaperAlignment = WallpaperAlignment
            };
            await _themeService.PreviewCustomThemeAsync(
                metadata,
                _wallpaperPreviewPrepared ? null : _wallpaperData,
                IsDarkPalette ? ApplicationElementTheme.Dark : ApplicationElementTheme.Light);
            _wallpaperPreviewPrepared = _wallpaperData != null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
        }
    }
}
