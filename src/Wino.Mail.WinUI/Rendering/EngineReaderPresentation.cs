using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Wino.Renderer;
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

    private readonly WindowHighContrastWatcher _highContrast;

    public EngineReaderPresentation(DispatcherQueue dispatcherQueue)
    {
        _highContrast = new WindowHighContrastWatcher(dispatcherQueue);
        _highContrast.Changed += (_, _) => HighContrastChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsDark { get; set; }

    public DarkModeMethod Method { get; set; }

    public string FontFamily { get; private set; } = DefaultFontFamily;

    public float FontSizePx { get; private set; } = DefaultFontSizePx;

    /// <summary>Raised on the UI thread when Windows turns high contrast on or off.</summary>
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
        ForcedColors = _highContrast.IsHighContrast
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

    /// <summary>
    /// Follows the high contrast state of the window that shows the view. Paired with <see cref="StopWatching"/>
    /// when the view leaves the tree.
    /// </summary>
    public void StartWatching(XamlRoot? xamlRoot) => _highContrast.Start(xamlRoot);

    public void StopWatching() => _highContrast.Stop();
}
