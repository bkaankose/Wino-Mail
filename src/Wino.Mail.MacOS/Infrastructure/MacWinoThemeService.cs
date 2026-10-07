using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// macOS theme service. Native materials stay in charge; a predefined Wino theme sets the
/// accent, forces light or dark where the Windows theme does, and supplies a backdrop tint
/// that the shell paints behind the mail list and reader (docs/macos-design-decisions.md).
/// Theme ids, accents and configuration keys match the Windows NewThemeService so a theme
/// chosen on one platform resolves to the same theme on the other. Custom themes are deferred.
/// </summary>
public sealed class MacWinoThemeService(IDispatcher dispatcher, IConfigurationService configuration, IPreferencesService preferences) : INewThemeService
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

    private ApplicationElementTheme _rootTheme;
    private string _accent = string.Empty;
    private Guid? _themeId;

    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;

    /// <summary>Raised on the UI thread whenever the theme, accent or backdrop setting changes.</summary>
    public static event EventHandler? AppearanceChanged;

    /// <summary>The current theme's accent, or null when no theme backdrop should be shown.</summary>
    public static NSColor? BackdropTint { get; private set; }

    /// <summary>The current theme's wallpaper (Resources/Themes/{name}.jpg), or null.</summary>
    public static NSImage? BackdropImage { get; private set; }

    /// <summary>The current gradient theme's stops as light start/end, dark start/end, or null.</summary>
    public static uint[]? BackdropGradient { get; private set; }

    /// <summary>True when a theme paints the window (wallpaper or gradient): panes then float as Wino zones.</summary>
    public static bool HasBackdrop => BackdropImage is not null || BackdropGradient is not null;

    /// <summary>Wallpaper used for a theme's preview tile, or null when it has none.</summary>
    public static NSImage? PreviewImage(string themeName) => LoadThemeImage(themeName);

    private static readonly Dictionary<string, NSImage?> ImageCache = new();

    private static NSImage? LoadThemeImage(string themeName)
    {
        if (ImageCache.TryGetValue(themeName, out var cached)) return cached;
        var path = Path.Combine(Foundation.NSBundle.MainBundle.ResourcePath ?? string.Empty, "Themes", themeName + ".jpg");
        var image = File.Exists(path) ? new NSImage(path) : null;
        ImageCache[themeName] = image;
        return image;
    }

    public bool IsCustomTheme => false;
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

    public Guid? CurrentApplicationThemeId
    {
        get => _themeId;
        set
        {
            _themeId = value;
            configuration.Set(CurrentApplicationThemeKey, value ?? DefaultThemeId);
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
        _themeId = stored is { } id && Themes.Any(theme => theme.Id == id) ? id : DefaultThemeId;
        await ApplyThemeToActiveWindowAsync();
    }

    public Task<List<AppThemeBase>> GetAvailableThemesAsync() => Task.FromResult(Themes.Cast<AppThemeBase>().ToList());

    public async Task SelectThemeAsync(Guid themeId, bool forceReapply = false)
    {
        var theme = Themes.FirstOrDefault(item => item.Id == themeId) ?? Themes[0];
        if (!forceReapply && theme.Id == _themeId) return;
        CurrentApplicationThemeId = theme.Id;
        _accent = theme.AccentColor ?? string.Empty;
        configuration.Set(AccentColorKey, _accent);
        AccentColorChanged?.Invoke(this, _accent);
        if (theme.ForceElementTheme != ApplicationElementTheme.Default)
        {
            _rootTheme = theme.ForceElementTheme;
            configuration.Set(SelectedAppThemeKey, _rootTheme);
            ElementThemeChanged?.Invoke(this, _rootTheme);
        }
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

    public Task ApplyThemeToActiveWindowAsync() => dispatcher.ExecuteOnUIThread(() =>
    {
        NSApplication.SharedApplication.Appearance = _rootTheme switch
        {
            ApplicationElementTheme.Dark => NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            ApplicationElementTheme.Light => NSAppearance.GetAppearance(NSAppearance.NameAqua),
            _ => null
        };
        var accent = WinoStyle.FromHexString(_accent);
        WinoStyle.AccentOverride = accent;
        var theme = Themes.FirstOrDefault(item => item.Id == _themeId);
        var hasTheme = theme is not null && theme.Id != DefaultThemeId && IsBackdropEnabled;
        BackdropTint = hasTheme ? accent : null;
        BackdropGradient = hasTheme && GradientThemes.TryGetValue(theme!.ThemeName, out var stops) ? stops : null;
        BackdropImage = hasTheme && BackdropGradient is null ? LoadThemeImage(theme!.ThemeName) : null;
        WinoStyle.HasBackdrop = HasBackdrop;
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    });

    public ThemeRuntimeState CaptureRuntimeState() => new(_themeId, _themeId ?? DefaultThemeId, _accent, _rootTheme);

    public Task RestoreRuntimeStateAsync(ThemeRuntimeState state)
    {
        _themeId = state.ThemeId;
        _accent = state.AccentColor ?? string.Empty;
        _rootTheme = state.ElementTheme;
        return ApplyThemeToActiveWindowAsync();
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

    // Custom themes are deferred on macOS (decision record).
    public Task ApplyCustomThemeAsync(bool isInitializing) => Task.CompletedTask;
    public Task<CustomThemeMetadata?> GetCustomThemeAsync(Guid themeId) => Task.FromResult<CustomThemeMetadata?>(null);
    public Task<List<CustomThemeMetadata>> GetCurrentCustomThemesAsync() => Task.FromResult(new List<CustomThemeMetadata>());
    public Task<bool> DeleteCustomThemeAsync(Guid themeId) => Task.FromResult(false);
    public Task<CustomThemeMetadata> SaveCustomThemeAsync(CustomThemeSaveRequest request)
        => Task.FromException<CustomThemeMetadata>(new PlatformNotSupportedException("Custom themes are not available on macOS yet."));
    public Task PreviewCustomThemeAsync(CustomThemeMetadata metadata, byte[]? wallpaperData, ApplicationElementTheme elementTheme) => Task.CompletedTask;

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
}
