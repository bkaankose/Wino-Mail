using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Renderer;
using Wino.Renderer.Primitives;
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

    // Found dates are marked like Outlook marks them: a dashed blue line, which reads as neither a link nor a spelling mark.
    private static readonly Rgba FoundItemLineLight = new(15, 108, 189, 255);
    private static readonly Rgba FoundItemLineDark = new(71, 158, 245, 255);

    public ThemeOptions CreateTheme() => new()
    {
        IsDark = IsDark,
        Method = Method,
        ForcedColors = IsHighContrast,
        HighlightUnderlineColor = IsDark ? FoundItemLineDark : FoundItemLineLight,
        HighlightUnderlineDashed = true
    };

    /// <summary>The system's font scale (Android, iOS Dynamic Type): the mail's text is zoomed by it. 1 on Windows.</summary>
    public float TextScale { get; set; } = 1;

    public TypographyOptions CreateTypography() => new(FontFamily, FontSizePx) { TextScale = TextScale };

    /// <summary>What the reader looks for after a message is drawn.</summary>
    public InsightKinds Insights { get; set; }

    /// <summary>The cultures dates are read in, highest priority first. Null lets the engine use the UI language.</summary>
    public IReadOnlyList<string>? Languages { get; set; }

    public ReaderOptions CreateOptions() => new()
    {
        Theme = CreateTheme(),
        Typography = CreateTypography(),
        Viewport = Viewport,
        BackgroundUpdates = true,
        Insights = Insights,
        Languages = Languages
    };

    private static bool SameLanguages(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
        => first is null || second is null ? first is null && second is null : first.SequenceEqual(second, StringComparer.OrdinalIgnoreCase);

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

        if (!SameLanguages(reader.Languages, Languages))
            reader.Languages = Languages;

        if (reader.InsightKinds != Insights)
            reader.InsightKinds = Insights;
    }
}
