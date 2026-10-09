using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
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
    /// <summary>
    /// The background of the gradient themes, copied from their Windows AppThemes/*.xaml
    /// (light start, light end, dark start, dark end; StartPoint 0,0 to EndPoint 0.32,1).
    /// </summary>
    private static readonly Dictionary<string, uint[]> GradientThemes = new()
    {
        ["Mist"] = [0xF4F7FA, 0xE1E7EF, 0x191D23, 0x0C0E12],
        ["Cocoa"] = [0xFBF7F2, 0xEDE3D8, 0x1D1815, 0x100D0B],
        ["Moss"] = [0xF1F6F1, 0xE0EBE1, 0x141A16, 0x0B0F0C],
        ["Rose"] = [0xFBF2F5, 0xF2E2E8, 0x1B1418, 0x100B0E],
        ["Indigo"] = [0xF1F3FB, 0xE1E5F5, 0x12141F, 0x0A0C14],
    };

    private const string SelectedAppThemeKey = "SelectedAppThemeKey";
    private const string AccentColorKey = "AccentColorKey";
    private const string CurrentApplicationThemeKey = "CurrentApplicationThemeKey";
    private const string BackdropEnabledKey = "MacThemeBackdropEnabled";

    public static readonly Guid DefaultThemeId = Guid.Empty;

    private static readonly MacPredefinedTheme[] Themes =
    [
        new("Default", DefaultThemeId, string.Empty, ApplicationElementTheme.Default),
        new("Nighty", Guid.Parse("5b65e04e-fd7e-4c2d-8221-068d3e02d23a"), "#e1b12c", ApplicationElementTheme.Dark),
        new("Forest", Guid.Parse("8bc89b37-a7c5-4049-86e2-de1ae8858dbd"), "#16a085", ApplicationElementTheme.Dark),
        new("Clouds", Guid.Parse("3b621cc2-e270-4a76-8477-737917cccda0"), "#0984e3", ApplicationElementTheme.Light),
        new("Snowflake", Guid.Parse("e143ddde-2e28-4846-9d98-dad63d6505f1"), "#4a69bd", ApplicationElementTheme.Light),
        new("Garden", Guid.Parse("698e4466-f88c-4799-9c61-f0ea1308ed49"), "#05c46b", ApplicationElementTheme.Light),
        new("Dunes", Guid.Parse("fff1d070-e7f2-4a91-8e50-30f20a19bf7d"), "#e17055", ApplicationElementTheme.Light),
        new("Lavender", Guid.Parse("41567cf1-31ca-4f8c-bd18-2f9a2d990cb5"), "#6c5ce7", ApplicationElementTheme.Light),
        new("Coastline", Guid.Parse("17434033-2152-41dc-bda6-933c52d250b4"), "#00a8b5", ApplicationElementTheme.Light),
        new("Blossom", Guid.Parse("02eb4827-50c7-4052-8dde-3a4af843317b"), "#e84393", ApplicationElementTheme.Light),
        new("Poppy", Guid.Parse("004c87bc-e540-406c-8c50-8eb8eb03d247"), "#d63031", ApplicationElementTheme.Light),
        new("Aurora", Guid.Parse("ba2974ca-7e03-4fe1-b102-efc6a6fa9cc9"), "#00cec9", ApplicationElementTheme.Dark),
        new("Ember", Guid.Parse("437d53b5-9228-408d-9374-c5ca457bf02f"), "#e58e26", ApplicationElementTheme.Dark),
        new("Nebula", Guid.Parse("b509ae0e-ca7a-4d53-b052-c3f30bbd4bd5"), "#8c7ae6", ApplicationElementTheme.Dark),
        new("Skyline", Guid.Parse("1ae2d96e-1aa8-4058-ad08-84f11069c69a"), "#0097e6", ApplicationElementTheme.Dark),
        new("Graphite", Guid.Parse("7e633982-ad91-4634-a926-25b154e7bfaf"), "#6b7b8c", ApplicationElementTheme.Dark),
        new("Mist", Guid.Parse("a0347ab8-d51e-411f-ab80-996d3b21a81b"), "#5e81ac", ApplicationElementTheme.Default),
        new("Cocoa", Guid.Parse("24b4f855-c9ea-485c-b9a9-0672d1b327dc"), "#c07a4c", ApplicationElementTheme.Default),
        new("Moss", Guid.Parse("e75ea27f-3757-4b46-8ad7-f5a4d4c58cdf"), "#2f9e63", ApplicationElementTheme.Default),
        new("Rose", Guid.Parse("ef2a1205-f6a4-4551-bac6-8747879669f7"), "#c4577e", ApplicationElementTheme.Default),
        new("Indigo", Guid.Parse("14c784d9-d8d3-462d-9b2c-965967d86d38"), "#4c5fd7", ApplicationElementTheme.Default)
    ];

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

    /// <summary>True when a theme paints the window (wallpaper or gradient): panes then float as Wino zones.</summary>
    public static bool HasBackdrop => BackdropImage is not null || BackdropGradient is not null;

    /// <summary>Wallpaper used for a predefined theme's preview tile, or null when it has none.</summary>
    public static NSImage? PreviewImage(string themeName) => LoadThemeImage(themeName);

    /// <summary>A gradient theme's stops (light start/end, dark start/end), or null.</summary>
    public static uint[]? GradientFor(string? themeName)
        => themeName is not null && GradientThemes.TryGetValue(themeName, out var stops) ? stops : null;

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
    public WindowBackdropType CurrentBackdropType { get; set; } = WindowBackdropType.None;

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

    public List<BackdropTypeWrapper> GetAvailableBackdropTypes() => [new(WindowBackdropType.None, "Default")];

    public void ApplyBackdrop(WindowBackdropType backdropType)
    {
        CurrentBackdropType = WindowBackdropType.None;
        BackdropChanged?.Invoke(this, CurrentBackdropType);
    }

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
        public MacPredefinedTheme(string name, Guid id, string accent, ApplicationElementTheme forced) : base(name, id)
        {
            AccentColor = accent;
            ForceElementTheme = forced;
            Compatibility = forced switch
            {
                ApplicationElementTheme.Dark => ThemeCompatibility.Dark,
                ApplicationElementTheme.Light => ThemeCompatibility.Light,
                _ => ThemeCompatibility.Both
            };
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
