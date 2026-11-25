using System;
using Wino.Renderer;
using Wino.Renderer.Theme;

namespace Wino.Mail.Rendering;

/// <summary>
/// Theme, typography and viewport of one reader view. A load takes them through <see cref="CreateOptions"/>;
/// <see cref="ApplyTo"/> moves a view that already shows a message to the current values. The head reports the
/// system's high contrast state through <see cref="IsHighContrast"/>.
/// </summary>
public sealed class EngineReaderPresentation
{
    private const string DefaultFontFamily = "Segoe UI";
    private const float DefaultFontSizePx = 15;

    private bool _isHighContrast;

    public bool IsDark { get; set; }

    public DarkModeMethod Method { get; set; }

    public string FontFamily { get; private set; } = DefaultFontFamily;

    public float FontSizePx { get; private set; } = DefaultFontSizePx;

    /// <summary>The viewport a load starts with; a phone head sets its screen size here.</summary>
    public ViewportOptions Viewport { get; set; } = new() { Fit = FitMode.ReflowThenShrink };

    public bool IsHighContrast
    {
        get => _isHighContrast;
        set
        {
            if (_isHighContrast == value)
                return;

            _isHighContrast = value;
            HighContrastChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread when the system turns high contrast on or off.</summary>
    public event EventHandler? HighContrastChanged;

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
        ForcedColors = IsHighContrast
    };

    /// <summary>The system's font scale (Android, iOS Dynamic Type): the mail's text is zoomed by it. 1 on Windows.</summary>
    public float TextScale { get; set; } = 1;

    public TypographyOptions CreateTypography() => new(FontFamily, FontSizePx) { TextScale = TextScale };

    public ReaderOptions CreateOptions() => new()
    {
        Theme = CreateTheme(),
        Typography = CreateTypography(),
        Viewport = Viewport,
        BackgroundUpdates = true
    };

    public void ApplyTo(IMailView view)
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
}
