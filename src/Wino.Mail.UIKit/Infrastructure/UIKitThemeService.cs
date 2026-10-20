using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Presentation;
using Wino.Presentation.UIKit;
using Wino.Services.Themes;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// iOS theme service, the UIKit twin of the Mac MacWinoThemeService. Theme ids, accents and configuration keys match
/// the Windows NewThemeService (<see cref="WinoThemeCatalog"/>), so a theme chosen on one platform resolves to the same
/// theme here. A theme sets the accent (window tint and <see cref="WinoStyle.AccentOverride"/>), may force light or dark
/// (<c>overrideUserInterfaceStyle</c>), and paints its wallpaper or gradient on every tracked
/// <see cref="ThemeBackdropView"/>; custom themes also recolour the zones through <see cref="WinoThemeSurfaces"/>.
/// </summary>
/// <remarks>
/// File and image work runs in the background; statics, surfaces and windows are touched on the UI thread inside
/// <see cref="ApplyThemeToActiveWindowAsync"/>, the single apply point.
/// </remarks>
public sealed class UIKitThemeService(IDispatcher dispatcher, IConfigurationService configuration, IPreferencesService preferences, CustomThemeFileStore store) : INewThemeService
{
    private const string SelectedAppThemeKey = WinoThemeCatalog.SelectedAppThemeKey;
    private const string AccentColorKey = WinoThemeCatalog.AccentColorKey;
    private const string CurrentApplicationThemeKey = WinoThemeCatalog.CurrentApplicationThemeKey;
    private const string BackdropEnabledKey = "MacThemeBackdropEnabled";

    public static readonly Guid DefaultThemeId = WinoThemeCatalog.DefaultThemeId;

    private static readonly PredefinedTheme[] Themes = WinoThemeCatalog.PredefinedThemes.Select(static theme => new PredefinedTheme(theme)).ToArray();
    private static readonly Dictionary<string, UIImage?> ImageCache = new();
    private static readonly List<WeakReference<ThemeBackdropView>> Backdrops = [];

    private sealed record PreviewState(CustomThemeMetadata Metadata, UIImage? Wallpaper, ApplicationElementTheme ElementTheme);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, UIImage?> _wallpapers = new();
    private List<CustomThemeMetadata> _customThemes = [];
    private ApplicationElementTheme _rootTheme;
    private string _accent = string.Empty;
    private Guid? _themeId;
    private PreviewState? _preview;
    private UIImage? _previewWallpaper;

    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;

    /// <summary>Raised on the UI thread whenever the theme, accent or backdrop changes.</summary>
    public static event EventHandler? AppearanceChanged;

    /// <summary>The current theme's wallpaper, or null.</summary>
    public static UIImage? BackdropImage { get; private set; }

    /// <summary>The current gradient theme's stops (light start/end, dark start/end), or null.</summary>
    public static uint[]? BackdropGradient { get; private set; }

    /// <summary>True when a theme paints the screen: panes then float as Wino zones.</summary>
    public static bool HasBackdrop => BackdropImage is not null || BackdropGradient is not null;

    /// <summary>The forced appearance of the current theme; Unspecified follows the system.</summary>
    public static UIUserInterfaceStyle InterfaceStyle { get; private set; } = UIUserInterfaceStyle.Unspecified;

    /// <summary>Keeps <paramref name="view"/> painted with the current theme. Main thread.</summary>
    public static ThemeBackdropView Track(ThemeBackdropView view)
    {
        Backdrops.RemoveAll(reference => !reference.TryGetTarget(out _));
        Backdrops.Add(new WeakReference<ThemeBackdropView>(view));
        view.SetBackdrop(BackdropImage, BackdropGradient);
        return view;
    }

    /// <summary>Applies the current appearance and tint to a window created after the theme loaded. Main thread.</summary>
    public static void ApplyTo(UIWindow window)
    {
        window.OverrideUserInterfaceStyle = InterfaceStyle;
        window.TintColor = WinoStyle.Accent;
    }

    /// <summary>A gradient theme's stops (light start/end, dark start/end), or null.</summary>
    public static uint[]? GradientFor(string? themeName)
        => WinoThemeCatalog.GradientFor(themeName) is { } gradient ? [gradient.LightStart, gradient.LightEnd, gradient.DarkStart, gradient.DarkEnd] : null;

    /// <summary>A predefined theme's wallpaper from the bundle's Themes folder, or null.</summary>
    public static UIImage? PreviewImage(string themeName) => LoadThemeImage(themeName);

    private static string ThemeImagePath(string themeName)
        => Path.Combine(NSBundle.MainBundle.ResourcePath ?? AppContext.BaseDirectory, "Themes", themeName + ".jpg");

    private static UIImage? LoadThemeImage(string themeName)
    {
        lock (ImageCache)
        {
            if (ImageCache.TryGetValue(themeName, out var cached)) return cached;
            var path = ThemeImagePath(themeName);
            var image = File.Exists(path) ? UIImage.FromFile(path) : null;
            ImageCache[themeName] = image;
            return image;
        }
    }

    public bool IsCustomTheme => _themeId is { } id && FindCachedCustom(id) is not null;

    // iOS has no window material choice; the system draws the bars.
    public WindowBackdropType CurrentBackdropType
    {
        get => WindowBackdropType.None;
        set { }
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

    /// <summary>Setting a different id (Wino Account restore) selects that theme right away, like the Windows setter.</summary>
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
        await dispatcher.ExecuteOnUIThread(() =>
        {
            WinoIcons.Set = preferences.IconSet;
            WinoIcons.Style = preferences.IconStyle;
        });
        preferences.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IPreferencesService.IconStyle))
                _ = dispatcher.ExecuteOnUIThread(() => WinoIcons.Style = preferences.IconStyle);
            else if (args.PropertyName == nameof(IPreferencesService.IconSet))
                _ = dispatcher.ExecuteOnUIThread(() => WinoIcons.Set = preferences.IconSet);
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
        themes.AddRange((await RefreshCustomThemesAsync()).Select(metadata => new CustomTheme(metadata, store.PreviewPath(metadata.Id))));
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

    /// <summary>A theme's accent (empty means the system accent); a predefined theme may force light or dark.</summary>
    private void ApplyThemeChoice(PredefinedTheme? predefined, CustomThemeMetadata? custom)
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
        UIColor.SystemBlue.GetRGBA(out var red, out var green, out var blue, out _);
        return $"#{(int)(red * 255):x2}{(int)(green * 255):x2}{(int)(blue * 255):x2}";
    }

    /// <summary>The single apply point: resolves the theme (or the editor preview) and paints it on the UI thread.</summary>
    public async Task ApplyThemeToActiveWindowAsync()
    {
        var preview = _preview;
        CustomThemeMetadata? custom = preview?.Metadata;
        UIImage? wallpaper = preview?.Wallpaper;
        if (preview is null && _themeId is { } id && FindPredefined(id) is null)
        {
            custom = FindCachedCustom(id);
            if (custom is not null) wallpaper = await LoadWallpaperAsync(id);
        }

        await dispatcher.ExecuteOnUIThread(() => ApplyOnUIThread(preview, custom, wallpaper));
    }

    private void ApplyOnUIThread(PreviewState? preview, CustomThemeMetadata? custom, UIImage? wallpaper)
    {
        var root = preview?.ElementTheme ?? _rootTheme;
        InterfaceStyle = root switch
        {
            ApplicationElementTheme.Dark => UIUserInterfaceStyle.Dark,
            ApplicationElementTheme.Light => UIUserInterfaceStyle.Light,
            _ => UIUserInterfaceStyle.Unspecified
        };
        WinoStyle.AccentOverride = WinoStyle.FromHexString(preview is null ? _accent : preview.Metadata.AccentColorHex);
        bool enabled = IsBackdropEnabled;

        if (custom is not null)
        {
            BackdropGradient = null;
            // A wallpaper that does not decode leaves the screen without a backdrop rather than failing.
            BackdropImage = enabled ? wallpaper : null;
            if (enabled) WinoThemeSurfaces.Apply(BuildSurfaces(custom));
            else WinoThemeSurfaces.Clear();
        }
        else
        {
            var theme = _themeId is { } id ? FindPredefined(id) : null;
            var themeName = theme is not null && theme.Id != DefaultThemeId && enabled ? theme.ThemeName : null;
            BackdropGradient = themeName is null ? null : GradientFor(themeName);
            BackdropImage = themeName is not null && BackdropGradient is null ? LoadThemeImage(themeName) : null;
            WinoThemeSurfaces.Clear();
        }

        WinoStyle.HasBackdrop = HasBackdrop;
        foreach (var window in AllWindows()) ApplyTo(window);
        Backdrops.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in Backdrops)
            if (reference.TryGetTarget(out var view)) view.SetBackdrop(BackdropImage, BackdropGradient);
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IEnumerable<UIWindow> AllWindows()
        => UIApplication.SharedApplication.ConnectedScenes.ToArray().OfType<UIWindowScene>().SelectMany(scene => scene.Windows);

    /// <summary>The custom palette per appearance, resolved like the Windows Custom.xaml dictionaries.</summary>
    private static Dictionary<WinoThemeSurface, (UIColor Light, UIColor Dark)> BuildSurfaces(CustomThemeMetadata metadata)
    {
        var light = metadata.LightPalette?.Resolve(false) ?? CustomThemePalette.CreateDefaults(false);
        var dark = metadata.DarkPalette?.Resolve(true) ?? CustomThemePalette.CreateDefaults(true);
        var result = new Dictionary<WinoThemeSurface, (UIColor Light, UIColor Dark)>();
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
        [new(WindowBackdropType.None, Translator.MacOS_Personalization_WindowMaterialDefault)];

    public void ApplyBackdrop(WindowBackdropType backdropType) => BackdropChanged?.Invoke(this, WindowBackdropType.None);

    // Windows-only presentation hooks: no UIKit equivalent is needed.
    public void ApplyIconStyle() { }
    public void UpdateSystemCaptionButtonColors() { }

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
                preview = UIKitThemeImaging.CreatePreviewJpeg(wallpaper)
                    ?? throw new CustomThemeCreationFailedException(Translator.MacPlatform_ThemeWallpaperInvalid);
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
        UIImage? wallpaper;
        if (wallpaperData is { Length: > 0 })
        {
            wallpaper = await Task.Run(() => UIKitThemeImaging.Decode(wallpaperData));
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

    private static PredefinedTheme? FindPredefined(Guid themeId) => Themes.FirstOrDefault(theme => theme.Id == themeId);

    private async Task<UIImage?> LoadWallpaperAsync(Guid themeId)
    {
        lock (_sync)
        {
            if (_wallpapers.TryGetValue(themeId, out var cached)) return cached;
        }
        var path = store.WallpaperPath(themeId);
        var image = await Task.Run(() => File.Exists(path) ? UIImage.FromFile(path) : null);
        if (image is null) Serilog.Log.Warning("The wallpaper of custom theme {ThemeId} could not be decoded; showing no backdrop.", themeId);
        lock (_sync) _wallpapers[themeId] = image;
        return image;
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Applying the theme failed."); }
    }

    private sealed class PredefinedTheme : AppThemeBase
    {
        public PredefinedTheme(WinoThemeDefinition definition) : base(definition.Name, definition.Id)
        {
            AccentColor = definition.AccentColor;
            ForceElementTheme = definition.ForcedElementTheme;
            Compatibility = definition.Compatibility;
        }

        public override AppThemeType AppThemeType => Id == DefaultThemeId ? AppThemeType.System : AppThemeType.PreDefined;
        public override Task<string> GetThemeResourceDictionaryContentAsync() => Task.FromResult(string.Empty);
        protected override Task<string> GetPreviewImagePathAsync()
        {
            var path = ThemeImagePath(ThemeName);
            return Task.FromResult(File.Exists(path) ? path : string.Empty);
        }
    }

    private sealed class CustomTheme : AppThemeBase
    {
        private readonly string _previewPath;

        public CustomTheme(CustomThemeMetadata metadata, string previewPath) : base(metadata.Name, metadata.Id)
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

/// <summary>Wallpaper decoding and the gallery thumbnail for custom themes (Mac MacThemeImaging).</summary>
public static class UIKitThemeImaging
{
    private const double PreviewWidth = 480;

    public static UIImage? Decode(byte[] data)
    {
        using var bytes = NSData.FromArray(data);
        return UIImage.LoadFromData(bytes);
    }

    /// <summary>A JPEG thumbnail of the wallpaper, or null when the bytes are not an image.</summary>
    public static byte[]? CreatePreviewJpeg(byte[] data)
    {
        using var image = Decode(data);
        if (image is null || image.Size.Width <= 0 || image.Size.Height <= 0) return null;
        var scale = Math.Min(1, PreviewWidth / image.Size.Width);
        var size = new CGSize(image.Size.Width * scale, image.Size.Height * scale);
        var format = new UIGraphicsImageRendererFormat { Scale = 1, Opaque = true };
        using var renderer = new UIGraphicsImageRenderer(size, format);
        using var thumbnail = renderer.CreateImage(_ => image.Draw(new CGRect(CGPoint.Empty, size)));
        using var jpeg = thumbnail.AsJPEG(0.82f);
        return jpeg?.ToArray();
    }
}
