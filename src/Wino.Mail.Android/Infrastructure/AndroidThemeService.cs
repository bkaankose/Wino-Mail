using Android.Content.Res;
using Android.Graphics;
using AndroidX.AppCompat.App;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Presentation;
using Wino.Presentation.Android;
using Wino.Services.Themes;
using Application = Android.App.Application;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// The Android theme service over the shared theme catalogue (the UIKit and Mac theme services' twin). Theme ids,
/// accents and configuration keys match the Windows NewThemeService (<see cref="WinoThemeCatalog"/>), so a theme chosen
/// on one platform resolves to the same theme here. A theme sets the accent (<see cref="WinoColors.AccentOverride"/> for
/// the Wino controls and Material widgets), may force light or dark (AppCompat night mode), and paints its wallpaper or
/// gradient behind every page (<see cref="WinoBackdrop"/>, which the blurred bars also draw); custom themes also recolour
/// the zones through <see cref="WinoThemeSurfaces"/>. The system theme (Default) has no backdrop, like Windows and iOS.
/// Icon style follows the preference (<see cref="WinoIcons.Style"/>). The system theme's accent is the device's Material
/// You accent on Android 12 and later (resource tokens in values-v31).
/// </summary>
/// <remarks>
/// File and image work runs in the background; statics are set on the UI thread in <see cref="ApplyThemeToActiveWindowAsync"/>,
/// the single apply point, which raises <see cref="AppearanceChanged"/>. The activity then repaints its wallpaper, or is
/// recreated when the accent or icon style changed (views take those colours and fonts when they are created).
/// </remarks>
public sealed class AndroidThemeService(IDispatcher dispatcher, IConfigurationService configuration, IPreferencesService preferences, CustomThemeFileStore store) : INewThemeService
{
    /// <summary>The iOS and Mac key: Off keeps plain surfaces with the theme accent only.</summary>
    private const string BackdropEnabledKey = "MacThemeBackdropEnabled";
    private const int PreviewWidth = 480;

    private static readonly Dictionary<string, Bitmap?> AssetCache = new();

    private sealed record PreviewState(CustomThemeMetadata Metadata, byte[]? Wallpaper, ApplicationElementTheme ElementTheme);

    /// <summary>What the backdrop shows for the current theme.</summary>
    private sealed record BackdropState(string? LightAsset, string? DarkAsset, WinoThemeGradient? Gradient, Bitmap? Custom);

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Bitmap?> _customWallpapers = new();
    private List<CustomThemeMetadata> _customThemes = [];
    // The user's Element Theme choice (saved), and the light or dark mode the active predefined theme forces (not
    // saved: like Windows, leaving that theme returns to the user's choice).
    private ApplicationElementTheme _rootTheme;
    private ApplicationElementTheme? _forcedTheme;
    private string _accent = string.Empty;
    private Guid? _themeId;
    private PreviewState? _preview;
    private byte[]? _previewWallpaper;
    private BackdropState _backdrop = new(null, null, null, null);
    private bool _preferencesHooked;

    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;

    /// <summary>Raised on the UI thread after the theme, accent, icon style or backdrop was applied.</summary>
    public event EventHandler? AppearanceChanged;

    public bool IsCustomTheme => _themeId is { } id && FindCachedCustom(id) is not null;

    // Android has no window material choice.
    public WindowBackdropType CurrentBackdropType
    {
        get => WindowBackdropType.None;
        set { }
    }

    /// <summary>The wallpaper or gradient behind the panes; Off keeps plain surfaces with the theme accent only.</summary>
    public bool IsBackdropEnabled
    {
        get => configuration.Get(BackdropEnabledKey, true);
        set
        {
            configuration.Set(BackdropEnabledKey, value);
            _ = ObserveAsync(ApplyThemeToActiveWindowAsync());
        }
    }

    public ApplicationElementTheme RootTheme
    {
        get => _forcedTheme ?? _rootTheme;
        set
        {
            _rootTheme = value;
            configuration.Set(WinoThemeCatalog.SelectedAppThemeKey, value);
            ElementThemeChanged?.Invoke(this, value);
            _ = ObserveAsync(ApplyThemeToActiveWindowAsync());
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
            configuration.Set(WinoThemeCatalog.CurrentApplicationThemeKey, value ?? WinoThemeCatalog.DefaultThemeId);
            _ = ObserveAsync(ActivateStoredThemeAsync());
        }
    }

    public string AccentColor
    {
        get => _accent;
        set
        {
            _accent = value ?? string.Empty;
            configuration.Set(WinoThemeCatalog.AccentColorKey, _accent);
            AccentColorChanged?.Invoke(this, _accent);
            _ = ObserveAsync(ApplyThemeToActiveWindowAsync());
        }
    }

    public async Task InitializeAsync()
    {
        _rootTheme = configuration.Get(WinoThemeCatalog.SelectedAppThemeKey, ApplicationElementTheme.Default);
        _accent = configuration.Get(WinoThemeCatalog.AccentColorKey, string.Empty) ?? string.Empty;
        if (!_preferencesHooked)
        {
            _preferencesHooked = true;
            preferences.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(IPreferencesService.IconStyle)) _ = ObserveAsync(ApplyThemeToActiveWindowAsync());
            };
        }
        var stored = configuration.Get<Guid?>(WinoThemeCatalog.CurrentApplicationThemeKey, null);
        _themeId = WinoThemeCatalog.DefaultThemeId;
        if (stored is { } id)
        {
            // Like Windows, custom themes are only read at launch when one is in use.
            if (WinoThemeCatalog.IsPredefined(id)) _themeId = id;
            else if ((await RefreshCustomThemesAsync()).Any(theme => theme.Id == id)) _themeId = id;
        }
        _forcedTheme = ForcedThemeOf(WinoThemeCatalog.Find(_themeId.Value));
        await ApplyThemeToActiveWindowAsync();
    }

    public async Task<List<AppThemeBase>> GetAvailableThemesAsync()
    {
        var themes = new List<AppThemeBase>(WinoThemeCatalog.PredefinedThemes.Select(static theme => new PredefinedTheme(theme)));
        themes.AddRange((await RefreshCustomThemesAsync()).Select(metadata => new CustomTheme(metadata, store.PreviewPath(metadata.Id))));
        foreach (var theme in themes) await theme.LoadPreviewImageAsync();
        return themes;
    }

    public async Task SelectThemeAsync(Guid themeId, bool forceReapply = false)
    {
        var predefined = WinoThemeCatalog.Find(themeId);
        var custom = predefined is null ? await FindCustomAsync(themeId) : null;
        if (predefined is null && custom is null)
            throw new InvalidOperationException($"Theme '{themeId}' is not available.");
        if (!forceReapply && themeId == _themeId && _preview is null) return;

        var previous = CaptureRuntimeState();
        try
        {
            _preview = null;
            _themeId = themeId;
            configuration.Set(WinoThemeCatalog.CurrentApplicationThemeKey, themeId);
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
    private void ApplyThemeChoice(WinoThemeDefinition? predefined, CustomThemeMetadata? custom)
    {
        _accent = predefined?.AccentColor ?? custom?.AccentColorHex ?? string.Empty;
        configuration.Set(WinoThemeCatalog.AccentColorKey, _accent);
        AccentColorChanged?.Invoke(this, _accent);
        var forced = ForcedThemeOf(predefined);
        if (forced == _forcedTheme) return;
        _forcedTheme = forced;
        ElementThemeChanged?.Invoke(this, RootTheme);
    }

    private static ApplicationElementTheme? ForcedThemeOf(WinoThemeDefinition? predefined)
        => predefined is { ForcedElementTheme: not ApplicationElementTheme.Default and var forced } ? forced : null;

    private async Task ActivateStoredThemeAsync()
    {
        var id = _themeId ?? WinoThemeCatalog.DefaultThemeId;
        var predefined = WinoThemeCatalog.Find(id);
        var custom = predefined is null ? await FindCustomAsync(id) : null;
        if (predefined is not null || custom is not null) ApplyThemeChoice(predefined, custom);
        await ApplyThemeToActiveWindowAsync();
    }

    public Task SetAccentColorAsync(string hexColor, bool preserveTheme = true)
    {
        AccentColor = hexColor;
        return Task.CompletedTask;
    }

    /// <summary>The system theme's resource accent: the device's Material You accent on Android 12 and later, else Wino blue.</summary>
    public string GetSystemAccentColorHex()
    {
        var context = Application.Context;
        var accent = WinoColors.FromResource(context, Resource.Color.wino_accent);
        return $"#{accent.R:x2}{accent.G:x2}{accent.B:x2}";
    }

    /// <summary>The single apply point: resolves the theme (or the editor preview) and applies it on the UI thread.</summary>
    public async Task ApplyThemeToActiveWindowAsync()
    {
        var preview = _preview;
        var custom = preview?.Metadata;
        Bitmap? customWallpaper = null;
        if (preview is not null)
            customWallpaper = preview.Wallpaper is { Length: > 0 } bytes ? await Task.Run(() => Decode(bytes)) : null;
        else if (_themeId is { } id && !WinoThemeCatalog.IsPredefined(id))
        {
            custom = FindCachedCustom(id);
            if (custom is not null) customWallpaper = await LoadCustomWallpaperAsync(id);
        }
        if (preview is not null && customWallpaper is null && preview.Metadata.Id != Guid.Empty)
            customWallpaper = await LoadCustomWallpaperAsync(preview.Metadata.Id);

        await dispatcher.ExecuteOnUIThread(() => ApplyOnUIThread(preview, custom, customWallpaper));
    }

    private void ApplyOnUIThread(PreviewState? preview, CustomThemeMetadata? custom, Bitmap? customWallpaper)
    {
        var root = preview?.ElementTheme ?? RootTheme;
        AppCompatDelegate.DefaultNightMode = root switch
        {
            ApplicationElementTheme.Dark => AppCompatDelegate.ModeNightYes,
            ApplicationElementTheme.Light => AppCompatDelegate.ModeNightNo,
            _ => AppCompatDelegate.ModeNightFollowSystem
        };
        WinoColors.AccentOverride = WinoColors.FromHexString(preview is null ? _accent : preview.Metadata.AccentColorHex);
        WinoIcons.Style = preferences.IconStyle;
        bool enabled = IsBackdropEnabled;

        if (custom is not null)
        {
            // A wallpaper that does not decode leaves the screen without a backdrop rather than failing.
            _backdrop = new BackdropState(null, null, null, enabled ? customWallpaper : null);
            if (enabled) WinoThemeSurfaces.Apply(BuildSurfaces(custom));
            else WinoThemeSurfaces.Clear();
        }
        else
        {
            var theme = _themeId is { } id ? WinoThemeCatalog.Find(id) : null;
            if (!enabled || theme is null || theme.IsSystemTheme) _backdrop = new BackdropState(null, null, null, null);
            else if (theme.Gradient is { } gradient) _backdrop = new BackdropState(null, null, gradient, null);
            else _backdrop = new BackdropState(theme.Name, theme.Name, null, null);
            WinoThemeSurfaces.Clear();
        }
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The wallpaper for <paramref name="resources"/>' appearance, or null for the plain backdrop colour. Main thread.</summary>
    public Bitmap? LoadWallpaper(Resources resources)
    {
        bool dark = (resources.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes;
        var state = _backdrop;
        if (state.Custom is { } custom) return custom;
        if (state.Gradient is { } gradient) return GradientBitmap(gradient, dark);
        return (dark ? state.DarkAsset : state.LightAsset) is { } name ? LoadAsset(name) : null;
    }

    /// <summary>A predefined theme's wallpaper from the APK's Themes folder (gallery thumbnails too); null when missing.</summary>
    public static Bitmap? LoadAsset(string themeName)
    {
        lock (AssetCache)
        {
            if (AssetCache.TryGetValue(themeName, out var cached)) return cached;
            Bitmap? bitmap = null;
            try
            {
                // Decoded no larger than about twice the screen: Nighty is 5472 x 3648 (80 MB as ARGB), more than the
                // whole Java heap of Android 6 devices.
                var path = $"Themes/{themeName}.jpg";
                if (Application.Context.Assets is { } assets)
                {
                    var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                    using (var probe = assets.Open(path)) BitmapFactory.DecodeStream(probe, null, bounds);
                    var metrics = Application.Context.Resources?.DisplayMetrics;
                    var longest = Math.Max(metrics?.WidthPixels ?? 1080, metrics?.HeightPixels ?? 1920);
                    var sample = 1;
                    while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= longest) sample *= 2;
                    using var stream = assets.Open(path);
                    bitmap = BitmapFactory.DecodeStream(stream, null, new BitmapFactory.Options { InSampleSize = sample });
                }
            }
            catch (Java.IO.IOException)
            {
                // Gradient themes and the system theme have no wallpaper of their own.
            }
            AssetCache[themeName] = bitmap;
            return bitmap;
        }
    }

    /// <summary>A gradient theme's backdrop as a small bitmap (Windows StartPoint 0,0 to EndPoint 0.32,1); scaled to fill.</summary>
    public static Bitmap GradientBitmap(WinoThemeGradient gradient, bool dark)
    {
        var key = $"gradient:{gradient.LightStart:x6}{gradient.LightEnd:x6}{gradient.DarkStart:x6}{gradient.DarkEnd:x6}:{dark}";
        lock (AssetCache)
        {
            if (AssetCache.TryGetValue(key, out var cached) && cached is not null) return cached;
            const int width = 270, height = 600;
            var bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888 ?? throw new InvalidOperationException("ARGB_8888 is unavailable."));
            using var canvas = new Canvas(bitmap);
            using var paint = new Paint();
            var start = WinoColors.Hex(dark ? gradient.DarkStart : gradient.LightStart);
            var end = WinoColors.Hex(dark ? gradient.DarkEnd : gradient.LightEnd);
            paint.SetShader(new LinearGradient(0, 0, width * 0.32f, height, start, end, WinoGraphics.Clamp));
            canvas.DrawRect(0, 0, width, height, paint);
            AssetCache[key] = bitmap;
            return bitmap;
        }
    }

    /// <summary>The custom palette per appearance, resolved like the Windows Custom.xaml dictionaries.</summary>
    private static Dictionary<WinoThemeSurface, (WinoRgba Light, WinoRgba Dark)> BuildSurfaces(CustomThemeMetadata metadata)
    {
        var light = metadata.LightPalette?.Resolve(false) ?? CustomThemePalette.CreateDefaults(false);
        var dark = metadata.DarkPalette?.Resolve(true) ?? CustomThemePalette.CreateDefaults(true);
        var result = new Dictionary<WinoThemeSurface, (WinoRgba Light, WinoRgba Dark)>();
        void Add(WinoThemeSurface surface, Func<CustomThemePalette, string?> read)
        {
            if (WinoRgba.FromHexString(read(light)) is { } lightColor && WinoRgba.FromHexString(read(dark)) is { } darkColor)
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

    public ThemeRuntimeState CaptureRuntimeState() => new(_themeId, _themeId ?? WinoThemeCatalog.DefaultThemeId, _accent, _rootTheme);

    public async Task RestoreRuntimeStateAsync(ThemeRuntimeState state)
    {
        _preview = null;
        _previewWallpaper = null;
        _themeId = state.ThemeId;
        configuration.Set(WinoThemeCatalog.CurrentApplicationThemeKey, state.ThemeId ?? WinoThemeCatalog.DefaultThemeId);
        _accent = state.AccentColor ?? string.Empty;
        _rootTheme = state.ElementTheme;
        _forcedTheme = state.ThemeId is { } restored ? ForcedThemeOf(WinoThemeCatalog.Find(restored)) : null;
        if (state.ThemeId is { } id && !WinoThemeCatalog.IsPredefined(id) && FindCachedCustom(id) is null)
            await RefreshCustomThemesAsync();
        AccentColorChanged?.Invoke(this, _accent);
        ElementThemeChanged?.Invoke(this, RootTheme);
        await ApplyThemeToActiveWindowAsync();
    }

    public List<string> GetAvailableAccountColors() =>
        ["#e74c3c", "#c0392b", "#e53935", "#d81b60", "#e91e63", "#ec407a", "#ff4081", "#9b59b6", "#8e44ad", "#673ab7", "#3f51b5", "#3498db", "#2980b9", "#03a9f4", "#00bcd4", "#009688", "#1abc9c", "#16a085", "#2ecc71", "#27ae60", "#4caf50", "#8bc34a", "#cddc39", "#f1c40f", "#f39c12", "#ff9800", "#e67e22", "#d35400", "#795548", "#607d8b"];

    public List<BackdropTypeWrapper> GetAvailableBackdropTypes() =>
        [new(WindowBackdropType.None, Translator.MacOS_Personalization_WindowMaterialDefault)];

    public void ApplyBackdrop(WindowBackdropType backdropType) => BackdropChanged?.Invoke(this, WindowBackdropType.None);

    // Windows-only presentation hooks: the icon style follows the preference in the apply point.
    public void ApplyIconStyle() => _ = ObserveAsync(ApplyThemeToActiveWindowAsync());
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
                preview = CreatePreviewJpeg(wallpaper)
                    ?? throw new CustomThemeCreationFailedException(Translator.MacPlatform_ThemeWallpaperInvalid);
            }
            await store.SaveAsync(savedTheme, wallpaper, preview).ConfigureAwait(false);
        });

        lock (_sync) _customWallpapers.Remove(savedTheme.Id);
        await RefreshCustomThemesAsync();
        return savedTheme;
    }

    public async Task<bool> DeleteCustomThemeAsync(Guid themeId)
    {
        if (await store.GetAsync(themeId) is null) return false;
        if (_themeId == themeId) await SelectThemeAsync(WinoThemeCatalog.DefaultThemeId, forceReapply: true);
        try
        {
            return await Task.Run(() => store.DeleteAsync(themeId));
        }
        finally
        {
            lock (_sync) _customWallpapers.Remove(themeId);
            await RefreshCustomThemesAsync();
        }
    }

    /// <summary>Shows a theme being edited without saving it; the editor sends the picked wallpaper bytes once.</summary>
    public async Task PreviewCustomThemeAsync(CustomThemeMetadata metadata, byte[]? wallpaperData, ApplicationElementTheme elementTheme)
    {
        if (wallpaperData is { Length: > 0 }) _previewWallpaper = wallpaperData;
        _preview = new PreviewState(metadata, wallpaperData is { Length: > 0 } ? wallpaperData : metadata.Id == Guid.Empty ? _previewWallpaper : null, elementTheme);
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

    private async Task<Bitmap?> LoadCustomWallpaperAsync(Guid themeId)
    {
        lock (_sync)
        {
            if (_customWallpapers.TryGetValue(themeId, out var cached)) return cached;
        }
        var path = store.WallpaperPath(themeId);
        var bitmap = await Task.Run(() => File.Exists(path) ? BitmapFactory.DecodeFile(path) : null);
        if (bitmap is null) Serilog.Log.Warning("The wallpaper of custom theme {ThemeId} could not be decoded; showing no backdrop.", themeId);
        lock (_sync) _customWallpapers[themeId] = bitmap;
        return bitmap;
    }

    public static Bitmap? Decode(byte[] data) => BitmapFactory.DecodeByteArray(data, 0, data.Length);

    /// <summary>A JPEG thumbnail of the wallpaper (480 px wide at most), or null when the bytes are not an image.</summary>
    public static byte[]? CreatePreviewJpeg(byte[] data)
    {
        using var image = Decode(data);
        if (image is null || image.Width <= 0 || image.Height <= 0) return null;
        var scale = Math.Min(1d, PreviewWidth / (double)image.Width);
        using var thumbnail = Bitmap.CreateScaledBitmap(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)), true);
        using var stream = new MemoryStream();
        var format = Bitmap.CompressFormat.Jpeg ?? throw new InvalidOperationException("JPEG compression is unavailable.");
        return thumbnail.Compress(format, 82, stream) ? stream.ToArray() : null;
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

        public override AppThemeType AppThemeType => Id == WinoThemeCatalog.DefaultThemeId ? AppThemeType.System : AppThemeType.PreDefined;
        public override Task<string> GetThemeResourceDictionaryContentAsync() => Task.FromResult(string.Empty);

        // Predefined wallpapers are APK assets, not files: the gallery loads them with LoadAsset.
        protected override Task<string> GetPreviewImagePathAsync() => Task.FromResult(string.Empty);
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

/// <summary>Whether the system appearance is dark.</summary>
public sealed class AndroidUnderlyingThemeService : IUnderlyingThemeService
{
    public bool IsUnderlyingThemeDark()
        => (Application.Context.Resources?.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes;
}
