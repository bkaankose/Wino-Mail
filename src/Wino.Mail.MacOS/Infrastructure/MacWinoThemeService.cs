using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Presentation;
using Wino.Presentation.AppKit;
using Wino.Services.Themes;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// macOS theme service. Native materials stay in charge; a predefined Wino theme sets the
/// accent, forces light or dark where the Windows theme does, and supplies a backdrop tint
/// that the shell paints behind the mail list and reader (docs/macos-design-decisions.md).
/// Theme ids, accents and configuration keys match the Windows NewThemeService so a theme
/// chosen on one platform resolves to the same theme on the other.
/// Custom themes use the Windows file layout (<see cref="CustomThemeFileStore"/>): their wallpaper is
/// painted by ThemeBackdropView with the theme's fit and focal point, and their palette recolours the
/// zones, reader, list header, sidebar and calendar slots through <see cref="WinoThemeSurfaces"/>.
/// </summary>
/// <remarks>
/// Threading: file and image work runs in the background; every static, the surfaces and the window
/// redraw are touched on the UI thread inside <see cref="ApplyThemeToActiveWindowAsync"/>, the single
/// apply point. The custom theme list and the wallpaper cache are guarded by one lock.
/// </remarks>
public sealed class MacWinoThemeService(IDispatcher dispatcher, IConfigurationService configuration, IPreferencesService preferences, CustomThemeFileStore store) : INewThemeService
{
    private const string SelectedAppThemeKey = WinoThemeCatalog.SelectedAppThemeKey;
    private const string AccentColorKey = WinoThemeCatalog.AccentColorKey;
    private const string CurrentApplicationThemeKey = WinoThemeCatalog.CurrentApplicationThemeKey;
    private const string BackdropEnabledKey = "MacThemeBackdropEnabled";
    private const string TranslucentWindowKey = "MacTranslucentWindow";

    public static readonly Guid DefaultThemeId = WinoThemeCatalog.DefaultThemeId;

    /// <summary>The shared predefined themes (<see cref="WinoThemeCatalog"/>) as gallery entries.</summary>
    private static readonly MacPredefinedTheme[] Themes = WinoThemeCatalog.PredefinedThemes.Select(static theme => new MacPredefinedTheme(theme)).ToArray();

    /// <summary>The editor's unsaved theme while it previews it; never persisted.</summary>
    private sealed record PreviewState(CustomThemeMetadata Metadata, NSImage? Wallpaper, ApplicationElementTheme ElementTheme);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, NSImage?> _wallpapers = new();
    private List<CustomThemeMetadata> _customThemes = [];
    private ApplicationElementTheme _rootTheme;
    private string _accent = string.Empty;
    private Guid? _themeId;
    private PreviewState? _preview;
    private NSImage? _previewWallpaper;
    private static bool _redrawPending;

    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;

    /// <summary>Raised on the UI thread whenever the theme, accent or backdrop setting changes.</summary>
    public static event EventHandler? AppearanceChanged;

    /// <summary>The current theme's accent, or null when no theme backdrop should be shown.</summary>
    public static NSColor? BackdropTint { get; private set; }

    /// <summary>The current theme's wallpaper (Resources/Themes/{name}.jpg or a custom wallpaper), or null.</summary>
    public static NSImage? BackdropImage { get; private set; }

    /// <summary>The current gradient theme's stops as light start/end, dark start/end, or null.</summary>
    public static uint[]? BackdropGradient { get; private set; }

    /// <summary>How a custom wallpaper fills the window. Predefined wallpapers always fill.</summary>
    public static ThemeWallpaperFit BackdropFit { get; private set; } = ThemeWallpaperFit.Fill;

    /// <summary>A custom wallpaper's focal point, or null for predefined wallpapers (anchored to the top).</summary>
    public static ThemeWallpaperAlignment? BackdropAlignment { get; private set; }

    /// <summary>
    /// True when the window shows the desktop through a vibrancy material (the Mac counterpart of the
    /// Windows Mica/Acrylic backdrop). A theme wallpaper or gradient still paints over it.
    /// </summary>
    public static bool IsTranslucentWindow { get; private set; }

    /// <summary>True when a theme or the translucent material paints the window: panes then float as Wino zones.</summary>
    public static bool HasBackdrop => BackdropImage is not null || BackdropGradient is not null || IsTranslucentWindow;

    /// <summary>Wallpaper used for a predefined theme's preview tile, or null when it has none.</summary>
    public static NSImage? PreviewImage(string themeName) => LoadThemeImage(themeName);

    /// <summary>A gradient theme's stops (light start/end, dark start/end), or null.</summary>
    public static uint[]? GradientFor(string? themeName)
        => WinoThemeCatalog.GradientFor(themeName) is { } gradient ? [gradient.LightStart, gradient.LightEnd, gradient.DarkStart, gradient.DarkEnd] : null;

    private static readonly Dictionary<string, NSImage?> ImageCache = new();

    private static NSImage? LoadThemeImage(string themeName)
    {
        lock (ImageCache)
        {
            if (ImageCache.TryGetValue(themeName, out var cached)) return cached;
            var path = Path.Combine(Foundation.NSBundle.MainBundle.ResourcePath ?? string.Empty, "Themes", themeName + ".jpg");
            var image = File.Exists(path) ? new NSImage(path) : null;
            ImageCache[themeName] = image;
            return image;
        }
    }

    public bool IsCustomTheme => _themeId is { } id && FindCachedCustom(id) is not null;
    public WindowBackdropType CurrentBackdropType
    {
        get => IsTranslucentWindowEnabled ? WindowBackdropType.DesktopAcrylic : WindowBackdropType.None;
        set => IsTranslucentWindowEnabled = value != WindowBackdropType.None;
    }

    /// <summary>The persisted translucent window choice; applying it redraws every Wino window.</summary>
    public bool IsTranslucentWindowEnabled
    {
        get => configuration.Get(TranslucentWindowKey, false);
        set
        {
            if (value == IsTranslucentWindowEnabled) return;
            configuration.Set(TranslucentWindowKey, value);
            BackdropChanged?.Invoke(this, CurrentBackdropType);
            _ = ApplyThemeToActiveWindowAsync();
        }
    }

    public bool IsBackdropEnabled
    {
        get => configuration.Get(BackdropEnabledKey, true);
        set { configuration.Set(BackdropEnabledKey, value); _ = ApplyThemeToActiveWindowAsync(); }
    }

    public ApplicationElementTheme RootTheme
    {
        get => _rootTheme;
        set
        {
            _rootTheme = value;
            configuration.Set(SelectedAppThemeKey, value);
            ElementThemeChanged?.Invoke(this, value);
            _ = ApplyThemeToActiveWindowAsync();
        }
    }

    /// <summary>
    /// Setting a different id (Wino Account restore) selects that theme right away, like the Windows
    /// setter. An id that exists on neither list stays stored and renders as Default.
    /// </summary>
    public Guid? CurrentApplicationThemeId
    {
        get => _themeId;
        set
        {
            if (value == _themeId) return;
            _themeId = value;
            configuration.Set(CurrentApplicationThemeKey, value ?? DefaultThemeId);
            _ = ObserveAsync(ActivateStoredThemeAsync());
        }
    }

    public string AccentColor
    {
        get => _accent;
        set
        {
            _accent = value ?? string.Empty;
            configuration.Set(AccentColorKey, _accent);
            AccentColorChanged?.Invoke(this, _accent);
            _ = ApplyThemeToActiveWindowAsync();
        }
    }

    public async Task InitializeAsync()
    {
        _rootTheme = configuration.Get(SelectedAppThemeKey, ApplicationElementTheme.Default);
        _accent = configuration.Get(AccentColorKey, string.Empty) ?? string.Empty;
        WinoIcons.Style = preferences.IconStyle;
        preferences.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IPreferencesService.IconStyle))
                _ = dispatcher.ExecuteOnUIThread(() => WinoIcons.Style = preferences.IconStyle);
        };
        var stored = configuration.Get<Guid?>(CurrentApplicationThemeKey, null);
        _themeId = DefaultThemeId;
        if (stored is { } id)
        {
            // Like Windows, custom themes are only read at launch when one is in use.
            if (FindPredefined(id) is not null) _themeId = id;
            else if ((await RefreshCustomThemesAsync()).Any(theme => theme.Id == id)) _themeId = id;
        }
        await ApplyThemeToActiveWindowAsync();
    }

    public async Task<List<AppThemeBase>> GetAvailableThemesAsync()
    {
        var themes = new List<AppThemeBase>(Themes);
        themes.AddRange((await RefreshCustomThemesAsync()).Select(metadata => new MacCustomTheme(metadata, store.PreviewPath(metadata.Id))));
        foreach (var theme in themes) await theme.LoadPreviewImageAsync();
        return themes;
    }

    public async Task SelectThemeAsync(Guid themeId, bool forceReapply = false)
    {
        var predefined = FindPredefined(themeId);
        var custom = predefined is null ? await FindCustomAsync(themeId) : null;
        if (predefined is null && custom is null)
            throw new InvalidOperationException($"Theme '{themeId}' is not available.");
        if (!forceReapply && themeId == _themeId && _preview is null) return;

        var previous = CaptureRuntimeState();
        try
        {
            _preview = null;
            _themeId = themeId;
            configuration.Set(CurrentApplicationThemeKey, themeId);
            ApplyThemeChoice(predefined, custom);
            await ApplyThemeToActiveWindowAsync();
        }
        catch
        {
            await RestoreRuntimeStateAsync(previous);
            throw;
        }
    }

    /// <summary>
    /// What selecting a theme does to accent and appearance (Windows ApplyCustomThemeCoreAsync): the
    /// theme's accent (empty means the system accent); a predefined theme may force light or dark,
    /// a custom theme keeps the saved appearance.
    /// </summary>
    private void ApplyThemeChoice(MacPredefinedTheme? predefined, CustomThemeMetadata? custom)
    {
        _accent = predefined?.AccentColor ?? custom?.AccentColorHex ?? string.Empty;
        configuration.Set(AccentColorKey, _accent);
        AccentColorChanged?.Invoke(this, _accent);
        if (predefined is { ForceElementTheme: not ApplicationElementTheme.Default } forced)
        {
            _rootTheme = forced.ForceElementTheme;
            configuration.Set(SelectedAppThemeKey, _rootTheme);
            ElementThemeChanged?.Invoke(this, _rootTheme);
        }
    }

    /// <summary>The id was set from outside (restore): resolve it and apply it like a selection.</summary>
    private async Task ActivateStoredThemeAsync()
    {
        var id = _themeId ?? DefaultThemeId;
        var predefined = FindPredefined(id);
        var custom = predefined is null ? await FindCustomAsync(id) : null;
        if (predefined is not null || custom is not null) ApplyThemeChoice(predefined, custom);
        await ApplyThemeToActiveWindowAsync();
    }

    public Task SetAccentColorAsync(string hexColor, bool preserveTheme = true)
    {
        AccentColor = hexColor;
        return Task.CompletedTask;
    }

    public string GetSystemAccentColorHex()
    {
        var color = NSColor.ControlAccent.UsingColorSpace(NSColorSpace.SRGBColorSpace);
        if (color is null) return "#007aff";
        return $"#{(int)(color.RedComponent * 255):x2}{(int)(color.GreenComponent * 255):x2}{(int)(color.BlueComponent * 255):x2}";
    }

    /// <summary>The single apply point: resolves the theme (or the editor preview) and paints it on the UI thread.</summary>
    public async Task ApplyThemeToActiveWindowAsync()
    {
        var preview = _preview;
        CustomThemeMetadata? custom = preview?.Metadata;
        NSImage? wallpaper = preview?.Wallpaper;
        if (preview is null && _themeId is { } id && FindPredefined(id) is null)
        {
            custom = FindCachedCustom(id);
            if (custom is not null) wallpaper = await LoadWallpaperAsync(id);
        }

        await dispatcher.ExecuteOnUIThread(() => ApplyOnUIThread(preview, custom, wallpaper));
    }

    private void ApplyOnUIThread(PreviewState? preview, CustomThemeMetadata? custom, NSImage? wallpaper)
    {
        var root = preview?.ElementTheme ?? _rootTheme;
        NSApplication.SharedApplication.Appearance = root switch
        {
            ApplicationElementTheme.Dark => NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            ApplicationElementTheme.Light => NSAppearance.GetAppearance(NSAppearance.NameAqua),
            _ => null
        };
        var accent = WinoStyle.FromHexString(preview is null ? _accent : preview.Metadata.AccentColorHex);
        WinoStyle.AccentOverride = accent;
        bool enabled = IsBackdropEnabled;
        IsTranslucentWindow = IsTranslucentWindowEnabled;

        if (custom is not null)
        {
            BackdropTint = enabled ? accent : null;
            BackdropGradient = null;
            // A wallpaper that does not decode leaves the window without a backdrop rather than failing.
            BackdropImage = enabled ? wallpaper : null;
            BackdropFit = custom.WallpaperFit;
            BackdropAlignment = custom.WallpaperFit == ThemeWallpaperFit.Fit ? ThemeWallpaperAlignment.Center : custom.WallpaperAlignment;
            if (enabled) WinoThemeSurfaces.Apply(BuildSurfaces(custom));
            else WinoThemeSurfaces.Clear();
        }
        else
        {
            var theme = _themeId is { } id ? FindPredefined(id) : null;
            var hasTheme = theme is not null && theme.Id != DefaultThemeId && enabled;
            BackdropTint = hasTheme ? accent : null;
            BackdropGradient = hasTheme ? GradientFor(theme!.ThemeName) : null;
            BackdropImage = hasTheme && BackdropGradient is null ? LoadThemeImage(theme!.ThemeName) : null;
            BackdropFit = ThemeWallpaperFit.Fill;
            BackdropAlignment = null;
            WinoThemeSurfaces.Clear();
        }

        WinoStyle.IsWindowMaterialOnly = IsTranslucentWindow && BackdropImage is null && BackdropGradient is null;
        WinoStyle.HasBackdrop = HasBackdrop;
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
        ScheduleRedraw();
    }

    /// <summary>The custom palette per appearance, resolved like the Windows Custom.xaml dictionaries.</summary>
    private static Dictionary<WinoThemeSurface, (NSColor Light, NSColor Dark)> BuildSurfaces(CustomThemeMetadata metadata)
    {
        var light = metadata.LightPalette?.Resolve(false) ?? CustomThemePalette.CreateDefaults(false);
        var dark = metadata.DarkPalette?.Resolve(true) ?? CustomThemePalette.CreateDefaults(true);
        var result = new Dictionary<WinoThemeSurface, (NSColor Light, NSColor Dark)>();
        void Add(WinoThemeSurface surface, Func<CustomThemePalette, string?> read)
        {
            if (WinoStyle.FromHexString(read(light)) is { } lightColor && WinoStyle.FromHexString(read(dark)) is { } darkColor)
                result[surface] = (lightColor, darkColor);
        }
        Add(WinoThemeSurface.Workspace, palette => palette.WinoContentZoneBackgroud);
        Add(WinoThemeSurface.ReadingPane, palette => palette.ReadingPaneBackgroundColorBrush);
        Add(WinoThemeSurface.MailListHeader, palette => palette.MailListHeaderBackgroundColor);
        Add(WinoThemeSurface.Navigation, palette => palette.NavigationViewContentBackground);
        Add(WinoThemeSurface.CalendarDefaultHour, palette => palette.CalendarDefaultHourBackgroundBrush);
        Add(WinoThemeSurface.CalendarWorkHour, palette => palette.CalendarWorkHourBackgroundBrush);
        return result;
    }

    /// <summary>One redraw of every window per run-loop turn, however many applies arrive (colour-well drags).</summary>
    private static void ScheduleRedraw()
    {
        if (_redrawPending) return;
        _redrawPending = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _redrawPending = false;
            foreach (var window in NSApplication.SharedApplication.DangerousWindows)
                if (window.ContentView is { } content) MarkForRedraw(content);
        });
    }

    private static void MarkForRedraw(NSView view)
    {
        view.NeedsDisplay = true;
        foreach (var subview in view.Subviews) MarkForRedraw(subview);
    }

    public ThemeRuntimeState CaptureRuntimeState() => new(_themeId, _themeId ?? DefaultThemeId, _accent, _rootTheme);

    public async Task RestoreRuntimeStateAsync(ThemeRuntimeState state)
    {
        _preview = null;
        _previewWallpaper = null;
        _themeId = state.ThemeId;
        configuration.Set(CurrentApplicationThemeKey, state.ThemeId ?? DefaultThemeId);
        _accent = state.AccentColor ?? string.Empty;
        _rootTheme = state.ElementTheme;
        if (state.ThemeId is { } id && FindPredefined(id) is null && FindCachedCustom(id) is null)
            await RefreshCustomThemesAsync();
        AccentColorChanged?.Invoke(this, _accent);
        ElementThemeChanged?.Invoke(this, _rootTheme);
        await ApplyThemeToActiveWindowAsync();
    }

    public List<string> GetAvailableAccountColors() =>
        ["#e74c3c", "#c0392b", "#e53935", "#d81b60", "#e91e63", "#ec407a", "#ff4081", "#9b59b6", "#8e44ad", "#673ab7", "#3f51b5", "#3498db", "#2980b9", "#03a9f4", "#00bcd4", "#009688", "#1abc9c", "#16a085", "#2ecc71", "#27ae60", "#4caf50", "#8bc34a", "#cddc39", "#f1c40f", "#f39c12", "#ff9800", "#e67e22", "#d35400", "#795548", "#607d8b"];

    public List<BackdropTypeWrapper> GetAvailableBackdropTypes() =>
        [new(WindowBackdropType.None, Translator.MacOS_Personalization_WindowMaterialDefault),
         new(WindowBackdropType.DesktopAcrylic, Translator.MacOS_Personalization_WindowMaterialTranslucent)];

    public void ApplyBackdrop(WindowBackdropType backdropType) => CurrentBackdropType = backdropType;

    // Windows-only presentation hooks: no native equivalent is needed on macOS.
    public void ApplyIconStyle() { }
    public void UpdateSystemCaptionButtonColors() { }

    // ---- Custom themes ----

    public Task ApplyCustomThemeAsync(bool isInitializing) => ApplyThemeToActiveWindowAsync();

    public async Task<CustomThemeMetadata?> GetCustomThemeAsync(Guid themeId)
        => (await RefreshCustomThemesAsync()).FirstOrDefault(theme => theme.Id == themeId);

    public Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync() => RefreshCustomThemesAsync();

    public async Task<CustomThemeMetadata> SaveCustomThemeAsync(CustomThemeSaveRequest request)
    {
        var themes = await RefreshCustomThemesAsync();
        var validationError = CustomThemeSaveValidator.Validate(request, themes);
        if (validationError != CustomThemeValidationError.None)
        {
            throw new CustomThemeCreationFailedException(validationError switch
            {
                CustomThemeValidationError.MissingName => Translator.Exception_CustomThemeMissingName,
                CustomThemeValidationError.MissingWallpaper => Translator.Exception_CustomThemeMissingWallpaper,
                CustomThemeValidationError.DuplicateName => Translator.Exception_CustomThemeExists,
                CustomThemeValidationError.MissingTheme => Translator.SettingsCustomTheme_DeleteMissing,
                CustomThemeValidationError.InvalidAccent => Translator.ApplicationThemeEditor_InvalidAccent,
                _ => Translator.ApplicationThemeEditor_InvalidSurface
            });
        }

        var accentColor = string.Empty;
        if (!string.IsNullOrWhiteSpace(request.AccentColorHex))
            ThemeColorValidator.TryNormalizeOpaque(request.AccentColorHex, out accentColor);

        var savedTheme = new CustomThemeMetadata
        {
            Id = request.ThemeId ?? Guid.NewGuid(),
            Name = request.Name.Trim(),
            AccentColorHex = accentColor,
            LightPalette = request.LightPalette,
            DarkPalette = request.DarkPalette,
            WallpaperFit = request.WallpaperFit,
            WallpaperAlignment = request.WallpaperFit == ThemeWallpaperFit.Fit ? ThemeWallpaperAlignment.Center : request.WallpaperAlignment
        };

        var wallpaper = request.WallpaperData is { Length: > 0 } data ? data : null;
        await Task.Run(async () =>
        {
            byte[]? preview = null;
            if (wallpaper is not null)
            {
                if (!MacThemeImaging.IsImage(wallpaper))
                    throw new CustomThemeCreationFailedException(Translator.MacPlatform_ThemeWallpaperInvalid);
                preview = MacThemeImaging.CreatePreviewJpeg(wallpaper);
            }
            await store.SaveAsync(savedTheme, wallpaper, preview).ConfigureAwait(false);
        });

        lock (_sync) _wallpapers.Remove(savedTheme.Id);
        await RefreshCustomThemesAsync();
        return savedTheme;
    }

    public async Task<bool> DeleteCustomThemeAsync(Guid themeId)
    {
        if (await store.GetAsync(themeId) is null) return false;

        // Default first, persisted, so the id never points at a theme whose files are gone.
        if (_themeId == themeId) await SelectThemeAsync(DefaultThemeId, forceReapply: true);

        try
        {
            return await Task.Run(() => store.DeleteAsync(themeId));
        }
        finally
        {
            lock (_sync) _wallpapers.Remove(themeId);
            await RefreshCustomThemesAsync();
        }
    }

    public async Task PreviewCustomThemeAsync(CustomThemeMetadata metadata, byte[]? wallpaperData, ApplicationElementTheme elementTheme)
    {
        NSImage? wallpaper;
        if (wallpaperData is { Length: > 0 })
        {
            wallpaper = await Task.Run(() => MacThemeImaging.Decode(wallpaperData));
            _previewWallpaper = wallpaper;
        }
        else if (metadata.Id != Guid.Empty)
        {
            wallpaper = await LoadWallpaperAsync(metadata.Id);
        }
        else
        {
            // The editor sends the picked bytes once; later previews reuse them.
            wallpaper = _previewWallpaper;
        }

        _preview = new PreviewState(metadata, wallpaper, elementTheme);
        await ApplyThemeToActiveWindowAsync();
    }

    private async Task<List<CustomThemeMetadata>> RefreshCustomThemesAsync()
    {
        var themes = await Task.Run(() => store.ListAsync());
        themes = themes.OrderBy(theme => theme.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        lock (_sync) _customThemes = themes;
        return new List<CustomThemeMetadata>(themes);
    }

    private async Task<CustomThemeMetadata?> FindCustomAsync(Guid themeId)
        => FindCachedCustom(themeId) ?? (await RefreshCustomThemesAsync()).FirstOrDefault(theme => theme.Id == themeId);

    private CustomThemeMetadata? FindCachedCustom(Guid themeId)
    {
        lock (_sync) return _customThemes.FirstOrDefault(theme => theme.Id == themeId);
    }

    private static MacPredefinedTheme? FindPredefined(Guid themeId) => Themes.FirstOrDefault(theme => theme.Id == themeId);

    /// <summary>A custom wallpaper, decoded once in the background and cached per theme until it is saved or deleted.</summary>
    private async Task<NSImage?> LoadWallpaperAsync(Guid themeId)
    {
        lock (_sync)
        {
            if (_wallpapers.TryGetValue(themeId, out var cached)) return cached;
        }
        var path = store.WallpaperPath(themeId);
        var image = await Task.Run(() => MacThemeImaging.DecodeFile(path));
        if (image is null) Serilog.Log.Warning("The wallpaper of custom theme {ThemeId} could not be decoded; showing no backdrop.", themeId);
        lock (_sync) _wallpapers[themeId] = image;
        return image;
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Applying the theme failed."); }
    }

    /// <summary>A predefined Wino theme; the preview is drawn natively from the accent.</summary>
    private sealed class MacPredefinedTheme : AppThemeBase
    {
        public MacPredefinedTheme(WinoThemeDefinition definition) : base(definition.Name, definition.Id)
        {
            AccentColor = definition.AccentColor;
            ForceElementTheme = definition.ForcedElementTheme;
            Compatibility = definition.Compatibility;
        }

        public override AppThemeType AppThemeType => Id == DefaultThemeId ? AppThemeType.System : AppThemeType.PreDefined;
        public override Task<string> GetThemeResourceDictionaryContentAsync() => Task.FromResult(string.Empty);
        protected override Task<string> GetPreviewImagePathAsync()
        {
            var path = Path.Combine(Foundation.NSBundle.MainBundle.ResourcePath ?? string.Empty, "Themes", ThemeName + ".jpg");
            return Task.FromResult(File.Exists(path) ? path : string.Empty);
        }
    }

    /// <summary>A custom theme from <see cref="CustomThemeFileStore"/>; its preview is the stored thumbnail.</summary>
    private sealed class MacCustomTheme : AppThemeBase
    {
        private readonly string _previewPath;

        public MacCustomTheme(CustomThemeMetadata metadata, string previewPath) : base(metadata.Name, metadata.Id)
        {
            _previewPath = previewPath;
            AccentColor = metadata.AccentColorHex;
            ForceElementTheme = ApplicationElementTheme.Default;
            Compatibility = ThemeCompatibility.Both;
        }

        public override AppThemeType AppThemeType => AppThemeType.Custom;
        public override Task<string> GetThemeResourceDictionaryContentAsync() => Task.FromResult(string.Empty);
        protected override Task<string> GetPreviewImagePathAsync() => Task.FromResult(File.Exists(_previewPath) ? _previewPath : string.Empty);
    }
}
