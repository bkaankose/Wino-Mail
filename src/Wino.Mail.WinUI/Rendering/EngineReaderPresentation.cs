using System;
using Microsoft.UI.Dispatching;
using Windows.UI;
using Windows.UI.ViewManagement;
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
    private readonly AccessibilitySettings? _accessibilitySettings;
    private bool _isWatching;

    public EngineReaderPresentation(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;

        try
        {
            _accessibilitySettings = new AccessibilitySettings();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "High contrast state is unavailable for the mail reader.");
        }
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

    private bool IsHighContrast()
    {
        try
        {
            return _accessibilitySettings?.HighContrast ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts reporting high contrast changes. Paired with <see cref="StopWatching"/> when the view leaves the tree,
    /// because the system object keeps its subscribers alive.
    /// </summary>
    public void StartWatching()
    {
        if (_isWatching || _accessibilitySettings is null)
            return;

        _isWatching = true;
        _accessibilitySettings.HighContrastChanged += OnHighContrastChanged;
    }

    public void StopWatching()
    {
        if (!_isWatching || _accessibilitySettings is null)
            return;

        _isWatching = false;
        _accessibilitySettings.HighContrastChanged -= OnHighContrastChanged;
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args)
        => _dispatcherQueue.TryEnqueue(() =>
        {
            if (_isWatching)
                HighContrastChanged?.Invoke(this, EventArgs.Empty);
        });
}
