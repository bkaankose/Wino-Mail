using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.System;
using Microsoft.UI.Xaml;
using Windows.UI;
using Wino.Renderer;
using Wino.Renderer.Primitives;
using Wino.Renderer.Theme;
using Wino.Renderer.WinUI;

namespace Wino.Mail.WinUI.Rendering;

/// <summary>
/// Theme, typography and viewport of one reader view. A load takes them through <see cref="CreateOptions"/>;
/// <see cref="ApplyTo"/> moves a view that already shows a message to the current values.
/// </summary>
public sealed partial class EngineReaderPresentation
{
    private const float ContentInsetPx = 16;
    private const string DefaultFontFamily = "Segoe UI";
    private const float DefaultFontSizePx = 15;

    private readonly DispatcherQueue _dispatcherQueue;
    private ThemeSettings? _themeSettings;

    public EngineReaderPresentation(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    public bool IsDark { get; set; }

    public DarkModeMethod Method { get; set; }

    public Rgba SurfaceColor { get; set; } = new(32, 32, 32, 255);

    public string FontFamily { get; private set; } = DefaultFontFamily;

    public float FontSizePx { get; private set; } = DefaultFontSizePx;

    /// <summary>Raised on the UI thread when Windows turns high contrast on or off.</summary>
    public event EventHandler? HighContrastChanged;

    public void SetSurfaceColor(Color color) => SurfaceColor = new Rgba(color.R, color.G, color.B, 255);

    public void SetTypography(string? fontFamily, double fontSize)
    {
        var family = fontFamily ?? string.Empty;
        var comma = family.IndexOf(',', StringComparison.Ordinal);
        if (comma >= 0)
            family = family[..comma];

        family = family.Trim().Trim('"', '\'');
        FontFamily = family.Length == 0 ? DefaultFontFamily : family;
        FontSizePx = fontSize is > 0 and < 200 ? (float)fontSize : DefaultFontSizePx;
    }

    public ThemeOptions CreateTheme() => new()
    {
        IsDark = IsDark,
        Method = Method,
        SurfaceColor = SurfaceColor,
        ForcedColors = IsHighContrast()
    };

    public TypographyOptions CreateTypography() => new(FontFamily, FontSizePx);

    public ReaderOptions CreateOptions() => new()
    {
        Theme = CreateTheme(),
        Typography = CreateTypography(),
        Viewport = new ViewportOptions { Fit = FitMode.ReflowThenShrink, ContentInset = ContentInsetPx },
        BackgroundUpdates = true
    };

    public void ApplyTo(MailView view)
    {
        if (view.Reader is not { } reader)
            return;

        var theme = CreateTheme();
        if (reader.Theme != theme)
            reader.Theme = theme;

        var typography = CreateTypography();
        if (reader.Typography != typography)
            reader.Typography = typography;
    }

    private bool IsHighContrast() => _themeSettings?.HighContrast ?? false;

    /// <summary>
    /// Follows the high contrast state of the window that shows the view. Paired with <see cref="StopWatching"/>
    /// when the view leaves the tree, because the system object keeps its subscribers alive.
    /// </summary>
    public void StartWatching(XamlRoot? xamlRoot)
    {
        StopWatching();

        if (xamlRoot?.ContentIslandEnvironment is not { } environment)
            return;

        try
        {
            _themeSettings = ThemeSettings.CreateForWindowId(environment.AppWindowId);
            _themeSettings.Changed += OnThemeSettingsChanged;
        }
        catch (Exception ex)
        {
            _themeSettings = null;
            Serilog.Log.Debug(ex, "High contrast state is unavailable for the mail reader.");
        }
    }

    public void StopWatching()
    {
        if (_themeSettings is not { } themeSettings)
            return;

        _themeSettings = null;
        themeSettings.Changed -= OnThemeSettingsChanged;
    }

    private void OnThemeSettingsChanged(ThemeSettings sender, object args)
        => _dispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(sender, _themeSettings))
                HighContrastChanged?.Invoke(this, EventArgs.Empty);
        });
}
