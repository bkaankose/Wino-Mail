using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Presentation;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;
using Wino.Services.Themes;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The Linux theme service over the shared theme catalogue, with the configuration keys every head uses, so a theme
/// chosen on one platform resolves to the same theme here. Light and dark go through <c>Adw.StyleManager</c>; the accent,
/// the wallpaper or gradient of a Wino theme and a custom theme's surfaces go through one CSS provider at
/// <see cref="WinoStyle.ThemePriority"/>, painted on <see cref="ThemeBackdrop"/>. <see cref="Apply"/> on the main thread is
/// the single apply point; it publishes the result in <see cref="WinoAppearance"/>. Window materials do not exist here:
/// the compositor draws the window.
/// </summary>
internal sealed partial class GtkThemeService(IDispatcher dispatcher, IConfigurationService configuration, IPreferencesService preferences, CustomThemeFileStore store,
    LinuxDesktopProfile profile) : INewThemeService
{
    private const string AdwaitaBlue = "#3584e4";

    private readonly object _sync = new();
    private List<CustomThemeMetadata> _customThemes = [];
    private ApplicationElementTheme _rootTheme;
    private string _accent = string.Empty;
    private Guid? _themeId;
    private volatile string _systemAccentHex = AdwaitaBlue;
    private Gtk.CssProvider? _provider;
    private bool _hooked;
    private bool _applying;

    public event EventHandler<ApplicationElementTheme>? ElementThemeChanged;
    public event EventHandler<string>? AccentColorChanged;
    public event EventHandler<WindowBackdropType>? BackdropChanged;

#if DEBUG
    public string LastCss { get; private set; } = string.Empty;
#endif

    public bool IsCustomTheme => _themeId is { } id && !WinoThemeCatalog.IsPredefined(id) && FindCachedCustom(id) is not null;

    public WindowBackdropType CurrentBackdropType
    {
        get => WindowBackdropType.None;
        set { }
    }

    public ApplicationElementTheme RootTheme
    {
        get => _rootTheme;
        set
        {
            _rootTheme = value;
            configuration.Set(WinoThemeCatalog.SelectedAppThemeKey, value);
            ElementThemeChanged?.Invoke(this, value);
            _ = ObserveAsync(ApplyThemeToActiveWindowAsync());
        }
    }

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

        // A stored id that is neither predefined nor a custom theme on this machine (restored from a Wino Account) stays
        // stored and shows as Default.
        var stored = configuration.Get<Guid?>(WinoThemeCatalog.CurrentApplicationThemeKey, null);
        _themeId = WinoThemeCatalog.DefaultThemeId;
        if (stored is { } id)
        {
            if (WinoThemeCatalog.IsPredefined(id)) _themeId = id;
            else if ((await RefreshCustomThemesAsync()).Any(theme => theme.Id == id)) _themeId = id;
        }

        if (!_hooked)
        {
            _hooked = true;
            // The service lives as long as the process, so these three subscriptions are never removed.
            preferences.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(IPreferencesService.IconStyle)) ApplyIconStyle();
            };
            await dispatcher.ExecuteOnUIThread(() =>
            {
                var styleManager = Adw.StyleManager.GetDefault();
                _ = GtkSignals.OnNotify(styleManager, "dark", DarkChanged);
                _ = GtkSignals.OnNotify(styleManager, "accent-color-rgba", SystemAccentChanged);
                HookDesktop();
            });
        }

        ApplyIconStyle();
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

    private void ApplyThemeChoice(WinoThemeDefinition? predefined, CustomThemeMetadata? custom)
    {
        _accent = predefined?.AccentColor ?? custom?.AccentColorHex ?? string.Empty;
        configuration.Set(WinoThemeCatalog.AccentColorKey, _accent);
        AccentColorChanged?.Invoke(this, _accent);
        if (predefined is { ForcedElementTheme: not ApplicationElementTheme.Default and var forced })
        {
            _rootTheme = forced;
            configuration.Set(WinoThemeCatalog.SelectedAppThemeKey, _rootTheme);
            ElementThemeChanged?.Invoke(this, _rootTheme);
        }
    }

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

    public string GetSystemAccentColorHex() => _systemAccentHex;

    public Task ApplyThemeToActiveWindowAsync() => dispatcher.ExecuteOnUIThread(Apply);

    public Task ApplyCustomThemeAsync(bool isInitializing) => ApplyThemeToActiveWindowAsync();

    private void Apply()
    {
        // Setting the colour scheme raises notify::dark synchronously, which would apply again.
        if (_applying) return;
        _applying = true;
        try
        {
            var styleManager = Adw.StyleManager.GetDefault();
            var preview = _preview;
            var themeId = _themeId ?? WinoThemeCatalog.DefaultThemeId;
            var definition = preview is null ? WinoThemeCatalog.Find(themeId) : null;
            var custom = preview?.Metadata ?? (definition is null ? FindCachedCustom(themeId) : null);
            if (definition is null && custom is null) definition = WinoThemeCatalog.Find(WinoThemeCatalog.DefaultThemeId);

            var root = preview?.ElementTheme
                ?? (definition is { ForcedElementTheme: not ApplicationElementTheme.Default and var forced } ? forced : _rootTheme);
            styleManager.SetColorScheme(root switch
            {
                ApplicationElementTheme.Dark => Adw.ColorScheme.ForceDark,
                ApplicationElementTheme.Light => Adw.ColorScheme.ForceLight,
                _ => Adw.ColorScheme.Default
            });
            var dark = styleManager.Dark;
            _systemAccentHex = SystemAccentHex(styleManager);

            var accent = WinoRgba.FromHexString(preview is null ? _accent : preview.Metadata.AccentColorHex);
            var input = new ThemeCssInput(accent, null, null, null, IsDark: dark);
            if (custom is not null)
            {
                var wallpaper = preview is null ? store.WallpaperPath(custom.Id) : PreviewWallpaperPath(preview);
                var palette = dark
                    ? custom.DarkPalette?.Resolve(true) ?? CustomThemePalette.CreateDefaults(true)
                    : custom.LightPalette?.Resolve(false) ?? CustomThemePalette.CreateDefaults(false);
                input = input with
                {
                    WallpaperPath = File.Exists(wallpaper) ? wallpaper : null,
                    Fit = custom.WallpaperFit,
                    Alignment = custom.WallpaperAlignment,
                    Surfaces = Surfaces(palette)
                };
            }
            else if (definition is { IsSystemTheme: false })
            {
                if (definition.Gradient is { } gradient)
                {
                    // A gradient theme's .jpg is only its gallery preview.
                    input = input with
                    {
                        GradientStart = WinoRgba.FromRgb(dark ? gradient.DarkStart : gradient.LightStart),
                        GradientEnd = WinoRgba.FromRgb(dark ? gradient.DarkEnd : gradient.LightEnd)
                    };
                }
                else if (ThemeImagePath(definition.Name) is var path && File.Exists(path))
                {
                    input = input with { WallpaperPath = path };
                }
            }

            input = ApplyDesktop(input, custom is null && definition?.Id == WinoThemeCatalog.DefaultThemeId, preview is not null, dark, out var paletteRevision);

            var css = ThemeCss.Build(input);
            if (EnsureProvider() is { } provider) provider.LoadFromString(css);
#if DEBUG
            LastCss = css;
#endif
            WinoAppearance.Update(ThemeCss.HasBackdrop(input), dark, input.Accent, paletteRevision, input.Tiling);
        }
        finally
        {
            _applying = false;
        }
    }

    private Gtk.CssProvider? EnsureProvider()
    {
        if (_provider is not null) return _provider;
        if (Gdk.Display.GetDefault() is not { } display) return null;
        var provider = Gtk.CssProvider.New();
        provider.OnParsingError += (_, args) => Serilog.Log.Warning("Wino theme stylesheet: {Message}", args.Error.Message);
        Gtk.StyleContext.AddProviderForDisplay(display, provider, WinoStyle.ThemePriority);
        _provider = provider;
        return provider;
    }

    private void DarkChanged()
    {
        if (_applying) return;
        Apply();
        ElementThemeChanged?.Invoke(this, RootTheme);
    }

    private void SystemAccentChanged()
    {
        if (_applying) return;
        Apply();
        if (_accent.Length == 0) AccentColorChanged?.Invoke(this, string.Empty);
    }

    private static string SystemAccentHex(Adw.StyleManager styleManager)
    {
        var color = styleManager.GetAccentColorRgba();
        static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
        return ThemeCss.Hex(new WinoRgba(Channel(color.Red), Channel(color.Green), Channel(color.Blue)));
    }

    private static Dictionary<WinoThemeSurface, WinoRgba> Surfaces(CustomThemePalette palette)
    {
        var result = new Dictionary<WinoThemeSurface, WinoRgba>();
        void Add(WinoThemeSurface surface, string? hex)
        {
            if (WinoRgba.FromHexString(hex) is { } color) result[surface] = color;
        }
        Add(WinoThemeSurface.Workspace, palette.WinoContentZoneBackgroud);
        Add(WinoThemeSurface.ReadingPane, palette.ReadingPaneBackgroundColorBrush);
        Add(WinoThemeSurface.MailListHeader, palette.MailListHeaderBackgroundColor);
        Add(WinoThemeSurface.Navigation, palette.NavigationViewContentBackground);
        Add(WinoThemeSurface.CalendarDefaultHour, palette.CalendarDefaultHourBackgroundBrush);
        Add(WinoThemeSurface.CalendarWorkHour, palette.CalendarWorkHourBackgroundBrush);
        return result;
    }

    private static string ThemeImagePath(string name) => Path.Combine(AppContext.BaseDirectory, "Themes", name + ".jpg");

    public ThemeRuntimeState CaptureRuntimeState() => new(_themeId, _themeId ?? WinoThemeCatalog.DefaultThemeId, _accent, _rootTheme);

    public async Task RestoreRuntimeStateAsync(ThemeRuntimeState state)
    {
        // Dropped first, or an unsaved editor preview stays on screen after Cancel.
        ClearPreview();
        _themeId = state.ThemeId;
        configuration.Set(WinoThemeCatalog.CurrentApplicationThemeKey, state.ThemeId ?? WinoThemeCatalog.DefaultThemeId);
        _accent = state.AccentColor ?? string.Empty;
        configuration.Set(WinoThemeCatalog.AccentColorKey, _accent);
        _rootTheme = state.ElementTheme;
        configuration.Set(WinoThemeCatalog.SelectedAppThemeKey, _rootTheme);
        if (state.ThemeId is { } id && !WinoThemeCatalog.IsPredefined(id) && FindCachedCustom(id) is null)
            await RefreshCustomThemesAsync();
        AccentColorChanged?.Invoke(this, _accent);
        ElementThemeChanged?.Invoke(this, _rootTheme);
        await ApplyThemeToActiveWindowAsync();
    }

    public List<string> GetAvailableAccountColors() =>
        ["#e74c3c", "#c0392b", "#e53935", "#d81b60", "#e91e63", "#ec407a", "#ff4081", "#9b59b6", "#8e44ad", "#673ab7", "#3f51b5", "#3498db", "#2980b9", "#03a9f4", "#00bcd4", "#009688", "#1abc9c", "#16a085", "#2ecc71", "#27ae60", "#4caf50", "#8bc34a", "#cddc39", "#f1c40f", "#f39c12", "#ff9800", "#e67e22", "#d35400", "#795548", "#607d8b"];

    public List<BackdropTypeWrapper> GetAvailableBackdropTypes() => [];

    public void ApplyBackdrop(WindowBackdropType backdropType) => BackdropChanged?.Invoke(this, WindowBackdropType.None);

    public void ApplyIconStyle() => _ = ObserveAsync(dispatcher.ExecuteOnUIThread(() => WinoAppearance.SetIconStyle(preferences.IconStyle)));

    public void UpdateSystemCaptionButtonColors() { }

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
        protected override Task<string> GetPreviewImagePathAsync()
            => Task.FromResult(ThemeImagePath(ThemeName) is var path && File.Exists(path) ? path : string.Empty);
    }
}
