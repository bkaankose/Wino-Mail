using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
    private readonly IWinoLogger? _logger;
    private ThemeRuntimeState? _originalRuntimeState;
    private Guid? _themeId;
    private byte[]? _wallpaperData;
    private bool _wallpaperPreviewPrepared;
    private bool _isInitializing;
    private bool _isNavigationCommitted;
    private NavigationResult? _pendingNavigationResult;

    private CustomThemePalette _lightPalette = new();
    private CustomThemePalette _darkPalette = new();

    /// <summary>
    /// The base surface, edited on its own because every other surface derives from it.
    /// </summary>
    public ObservableCollection<ThemePaletteColorOptionViewModel> BaseSurfaceOptions { get; } = [];

    public ObservableCollection<ThemePaletteColorOptionViewModel> SurfaceOptions { get; } = [];
    public ObservableCollection<ThemePaletteColorOptionViewModel> CalendarOptions { get; } = [];
    public IReadOnlyList<ThemeBasePreset> BasePresets { get; } = ThemeBasePresets.Create();

    [ObservableProperty] public partial string ThemeName { get; set; } = string.Empty;
    [ObservableProperty] public partial string WallpaperPreviewPath { get; set; } = string.Empty;
    [ObservableProperty] public partial string WallpaperFileName { get; set; } = string.Empty;
    [ObservableProperty] public partial bool UseSystemAccent { get; set; } = true;
    [ObservableProperty] public partial string AccentColorHex { get; set; } = string.Empty;
    [ObservableProperty] public partial ThemeWallpaperFit WallpaperFit { get; set; } = ThemeWallpaperFit.Fill;
    [ObservableProperty] public partial ThemeWallpaperAlignment WallpaperAlignment { get; set; } = ThemeWallpaperAlignment.Center;
    [ObservableProperty] public partial bool IsDarkPalette { get; set; }
    [ObservableProperty] public partial bool IsDirty { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))] public partial bool IsSaving { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))] public partial bool IsDeleting { get; set; }
    [ObservableProperty] public partial bool IsErrorOpen { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// The resolved palette of the mode being edited. A new instance is published on every
    /// change so surface previews can rebind without the palette raising notifications itself.
    /// </summary>
    [ObservableProperty] public partial CustomThemePalette PreviewPalette { get; set; } = CustomThemePalette.CreateDefaults(false);

    /// <summary>
    /// The average color of the wallpaper, supplied by the view. Translucent surfaces are
    /// measured against it, so contrast is not reported until the view provides one.
    /// </summary>
    [ObservableProperty] public partial string WallpaperAverageColor { get; set; } = string.Empty;

    [ObservableProperty] public partial int OverriddenSurfaceCount { get; set; }
    [ObservableProperty] public partial int OverriddenCalendarCount { get; set; }
    [ObservableProperty] public partial bool HasLightOverrides { get; set; }
    [ObservableProperty] public partial bool HasDarkOverrides { get; set; }

    public bool IsEditMode => _themeId.HasValue;
    public bool HasWallpaper => !string.IsNullOrWhiteSpace(WallpaperPreviewPath);
    public bool IsFocalSelectorEnabled => WallpaperFit == ThemeWallpaperFit.Fill && HasWallpaper;
    public string PageTitle => IsEditMode ? Translator.ApplicationThemeEditor_EditTitle : Translator.ApplicationThemeEditor_CreateTitle;
    // Segmented selection is read only here and pushed into the control by the view.
    // A two way SelectedIndex binding lets the control write its own load time value
    // back and silently reset the palette being edited.
    public int PaletteModeIndex => IsDarkPalette ? 1 : 0;

    public int WallpaperFitIndex => WallpaperFit == ThemeWallpaperFit.Fill ? 0 : 1;

    /// <summary>Applies a palette selection made by the user in the segmented control.</summary>
    public void SelectPaletteMode(int index) => IsDarkPalette = index == 1;

    /// <summary>Applies a wallpaper fit selection made by the user in the segmented control.</summary>
    public void SelectWallpaperFit(int index)
        => WallpaperFit = index == 1 ? ThemeWallpaperFit.Fit : ThemeWallpaperFit.Fill;

    public string CopyPaletteLabel => IsDarkPalette
        ? Translator.ApplicationThemeEditor_CopyToLight
        : Translator.ApplicationThemeEditor_CopyToDark;

    public string FocalPointDescription => WallpaperFit == ThemeWallpaperFit.Fill
        ? Translator.ApplicationThemeEditor_FocalPointDescription
        : Translator.ApplicationThemeEditor_FocalPointFitDescription;

    public ApplicationThemeEditorPageViewModel(INewThemeService themeService, IDialogServiceBase dialogService, IWinoLogger? logger = null)
    {
        _themeService = themeService;
        _dialogService = dialogService;
        _logger = logger;
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
                WallpaperFileName = metadata.Name;
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
                WallpaperFileName = string.Empty;
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
            RaisePaletteSelectionChanged();
            RaiseWallpaperFitSelectionChanged();
            OnPropertyChanged(nameof(HasWallpaper));
            OnPropertyChanged(nameof(IsFocalSelectorEnabled));
            OnPropertyChanged(nameof(FocalPointDescription));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
        }
        finally
        {
            _isInitializing = false;
            DeleteCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Picks the palette to edit from what the application actually renders. It applies only
    /// when the theme setting is Default, because an explicit Light or Dark choice already
    /// says which palette the user is looking at.
    /// </summary>
    /// <param name="isDark">True when the shell is currently rendering dark.</param>
    public void ResolveSystemPaletteMode(bool isDark)
    {
        if (_originalRuntimeState?.ElementTheme != ApplicationElementTheme.Default || IsDarkPalette == isDark)
            return;

        var wasInitializing = _isInitializing;
        _isInitializing = true;

        IsDarkPalette = isDark;

        _isInitializing = wasInitializing;
    }

    [RelayCommand]
    private async Task ChooseWallpaperAsync()
    {
        var files = await _dialogService.PickFilesAsync(".jpg", ".jpeg", ".png");
        var file = files?.Count > 0 ? files[0] : null;

        if (file == null)
            return;

        ApplyWallpaper(file.Data, file.FullFilePath, file.FileName);
    }

    /// <summary>
    /// Accepts a wallpaper from any source, including a file dropped on the editor.
    /// </summary>
    public void ApplyWallpaper(byte[] data, string fullFilePath, string fileName)
    {
        _wallpaperData = data;
        _wallpaperPreviewPrepared = false;
        WallpaperFileName = fileName;
        WallpaperPreviewPath = new Uri(fullFilePath).AbsoluteUri;
        MarkDirtyAndPreview();
    }

    /// <summary>
    /// Expands one color, collapsing any other, so a single surface preview is open at a time.
    /// </summary>
    [RelayCommand]
    private void ToggleOption(ThemePaletteColorOptionViewModel? option)
    {
        if (option == null)
            return;

        var expand = !option.IsExpanded;

        foreach (var candidate in AllOptions())
            candidate.IsExpanded = false;

        option.IsExpanded = expand;
    }

    [RelayCommand]
    private void ResetColor(ThemePaletteColorOptionViewModel? option)
    {
        if (option == null)
            return;

        CurrentPalette.ResetOverride(option.Key);
        RebuildPaletteOptions(option.Key);
        MarkDirtyAndPreview();
    }

    [RelayCommand]
    private void ApplyBasePreset(ThemeBasePreset? preset)
    {
        if (preset == null)
            return;

        _lightPalette.SetOverride(CustomThemeColorKey.BaseSurface, preset.LightColor);
        _darkPalette.SetOverride(CustomThemeColorKey.BaseSurface, preset.DarkColor);
        RebuildPaletteOptions();
        MarkDirtyAndPreview();
    }

    /// <summary>
    /// Copies the palette being edited onto the other mode, so both do not have to be built by hand.
    /// </summary>
    [RelayCommand]
    private void CopyPaletteToOtherMode()
    {
        if (IsDarkPalette)
            _lightPalette = _darkPalette.Clone();
        else
            _darkPalette = _lightPalette.Clone();

        UpdateOverrideCounts();
        MarkDirty();
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

    private bool CanDelete() => IsEditMode && !_isInitializing && !IsSaving && !IsDeleting;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (!CanDelete() || _themeId is not Guid themeId)
            return;

        IsDeleting = true;
        IsErrorOpen = false;

        try
        {
            var metadata = await _themeService.GetCustomThemeAsync(themeId)
                ?? throw new InvalidOperationException(Translator.SettingsCustomTheme_DeleteMissing);
            var confirmed = await _dialogService.ShowConfirmationDialogAsync(
                string.Format(Translator.SettingsCustomTheme_DeleteConfirm_Message, metadata.Name),
                Translator.SettingsCustomTheme_DeleteConfirm_Title,
                Translator.Buttons_Delete);

            if (!confirmed)
                return;

            // End the editor preview before deletion so the service can select its
            // fallback when deleting the active theme, or preserve the other theme.
            if (_originalRuntimeState != null)
                await _themeService.RestoreRuntimeStateAsync(_originalRuntimeState);

            if (!await _themeService.DeleteCustomThemeAsync(themeId))
                throw new InvalidOperationException(Translator.SettingsCustomTheme_DeleteMissing);

            _isNavigationCommitted = true;
            IsDirty = false;
            _pendingNavigationResult = NavigationResult.Deleted(themeId);
            WeakReferenceMessenger.Default.Send(new BackBreadcrumNavigationRequested(Result: TakeNavigationResult()));
        }
        catch (Exception ex)
        {
            _logger?.CaptureException(ex, "DeleteCustomTheme");
            ErrorMessage = ex.Message;
            IsErrorOpen = true;
        }
        finally
        {
            IsDeleting = false;
        }
    }

    private bool CanSave() => !IsSaving && !IsDeleting;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(ThemeName))
        {
            ErrorMessage = Translator.ApplicationThemeEditor_NameRequired;
            IsErrorOpen = true;
            return;
        }

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
        if (IsDeleting && !_isNavigationCommitted)
            return false;

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
    partial void OnWallpaperAverageColorChanged(string value) => UpdateContrastReports();

    partial void OnWallpaperPreviewPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasWallpaper));
        OnPropertyChanged(nameof(IsFocalSelectorEnabled));
    }

    partial void OnWallpaperFitChanged(ThemeWallpaperFit value)
    {
        if (value == ThemeWallpaperFit.Fit)
            WallpaperAlignment = ThemeWallpaperAlignment.Center;

        RaiseWallpaperFitSelectionChanged();
        OnPropertyChanged(nameof(IsFocalSelectorEnabled));
        OnPropertyChanged(nameof(FocalPointDescription));
        MarkDirtyAndPreview();
    }

    partial void OnIsDarkPaletteChanged(bool value)
    {
        RaisePaletteSelectionChanged();
        OnPropertyChanged(nameof(CopyPaletteLabel));
        RebuildPaletteOptions();

        if (!_isInitializing)
            _ = PreviewAsync();
    }

    private void RaisePaletteSelectionChanged() => OnPropertyChanged(nameof(PaletteModeIndex));

    private void RaiseWallpaperFitSelectionChanged() => OnPropertyChanged(nameof(WallpaperFitIndex));

    private CustomThemePalette CurrentPalette => IsDarkPalette ? _darkPalette : _lightPalette;

    private IEnumerable<ThemePaletteColorOptionViewModel> AllOptions()
        => BaseSurfaceOptions.Concat(SurfaceOptions).Concat(CalendarOptions);

    /// <summary>
    /// Rebuilds every editable color from the palette of the current mode.
    /// </summary>
    /// <param name="keyToKeepExpanded">The color whose preview stays open across the rebuild.</param>
    private void RebuildPaletteOptions(CustomThemeColorKey? keyToKeepExpanded = null)
    {
        var wasInitializing = _isInitializing;
        _isInitializing = true;

        var expandedKey = keyToKeepExpanded ?? AllOptions().FirstOrDefault(option => option.IsExpanded)?.Key;

        ClearOptions(BaseSurfaceOptions);
        ClearOptions(SurfaceOptions);
        ClearOptions(CalendarOptions);

        var resolved = CurrentPalette.Resolve(IsDarkPalette);

        AddOption(BaseSurfaceOptions, CustomThemeColorKey.BaseSurface, resolved.MainCustomThemeColor, expandedKey);

        foreach (var key in CustomThemeColorCatalog.SurfaceKeys)
            AddOption(SurfaceOptions, key, GetResolvedValue(resolved, key), expandedKey);

        foreach (var key in CustomThemeColorCatalog.CalendarKeys)
            AddOption(CalendarOptions, key, GetResolvedValue(resolved, key), expandedKey);

        UpdateOverrideCounts();
        UpdateContrastReports();
        PreviewPalette = resolved;

        _isInitializing = wasInitializing;
    }

    private static string? GetResolvedValue(CustomThemePalette resolved, CustomThemeColorKey key)
        => resolved.GetOverride(key);

    private void ClearOptions(ObservableCollection<ThemePaletteColorOptionViewModel> options)
    {
        foreach (var option in options)
            option.PropertyChanged -= PaletteOptionChanged;

        options.Clear();
    }

    private void AddOption(
        ObservableCollection<ThemePaletteColorOptionViewModel> options,
        CustomThemeColorKey key,
        string? resolvedValue,
        CustomThemeColorKey? expandedKey)
    {
        var option = new ThemePaletteColorOptionViewModel(
            key,
            resolvedValue ?? string.Empty,
            !string.IsNullOrWhiteSpace(CurrentPalette.GetOverride(key)))
        {
            IsExpanded = expandedKey == key
        };

        option.PropertyChanged += PaletteOptionChanged;
        options.Add(option);
    }

    private void PaletteOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing || sender is not ThemePaletteColorOptionViewModel option || e.PropertyName != nameof(option.Value))
            return;

        CurrentPalette.SetOverride(option.Key, option.Value);
        option.IsOverridden = true;
        UpdateOverrideCounts();

        PreviewPalette = CurrentPalette.Resolve(IsDarkPalette);
        UpdateContrastReports();
        MarkDirtyAndPreview();
    }

    private void UpdateOverrideCounts()
    {
        OverriddenSurfaceCount = CustomThemeColorCatalog.SurfaceKeys.Count(key => !string.IsNullOrWhiteSpace(CurrentPalette.GetOverride(key)));
        OverriddenCalendarCount = CustomThemeColorCatalog.CalendarKeys.Count(key => !string.IsNullOrWhiteSpace(CurrentPalette.GetOverride(key)));
        HasLightOverrides = HasAnyOverride(_lightPalette);
        HasDarkOverrides = HasAnyOverride(_darkPalette);
    }

    private static bool HasAnyOverride(CustomThemePalette palette)
        => Enum.GetValues<CustomThemeColorKey>().Any(key => !string.IsNullOrWhiteSpace(palette.GetOverride(key)));

    private void UpdateContrastReports()
    {
        var baseSurface = BaseSurfaceOptions.FirstOrDefault()?.Value;

        foreach (var option in AllOptions())
        {
            if (!option.CarriesBodyText)
                continue;

            option.ReportContrast(ThemeContrastCalculator.TryGetBodyTextContrast(option.Value, baseSurface, WallpaperAverageColor));
        }
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
