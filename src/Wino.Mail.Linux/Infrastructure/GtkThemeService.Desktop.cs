using Wino.Core.Domain.Models.Personalization;
using Wino.Platform.Linux.Desktop;
using Wino.Presentation;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The desktop profile's part of the theme: window buttons and the flat look of the Tiling style, and the Omarchy
/// palette. The palette replaces libadwaita's colours only while Omarchy is detected, the user follows it, the Default
/// Wino theme is selected without an editor preview, and the palette's light or dark mode is the applied one; an
/// accent the user picked in Wino still wins over the palette's.
/// </summary>
internal sealed partial class GtkThemeService
{
    private OmarchyThemeWatcher? _omarchyWatcher;
    private OmarchyPalette? _omarchyPalette;
    private ThemePalette? _themePalette;
    private int _paletteRevision;
    private bool _desktopHooked;

    /// <summary>The palette in use, or null; <see cref="DesktopStatus"/> says why not.</summary>
    public ThemePalette? AppliedPalette { get; private set; }

    public OmarchyPalette? LoadedOmarchyPalette => _omarchyPalette;

    public string DesktopStatus { get; private set; } = string.Empty;

    /// <summary>On the main thread, once: loads the palette, starts the watcher and follows the profile.</summary>
    private void HookDesktop()
    {
        if (_desktopHooked) return;
        _desktopHooked = true;
        // Both live as long as the process.
        profile.Changed += () => _ = ObserveAsync(dispatcher.ExecuteOnUIThread(DesktopProfileChanged));
        WatchOmarchy();
    }

    private void DesktopProfileChanged()
    {
        WatchOmarchy();
        Apply();
    }

    private void WatchOmarchy()
    {
        var folder = profile.Environment.IsOmarchy ? profile.Environment.OmarchyCurrentFolder : null;
        if (_omarchyWatcher?.CurrentFolder == folder && (folder is null || _omarchyPalette is not null)) return;
        _omarchyWatcher?.Dispose();
        _omarchyWatcher = folder is null ? null : new OmarchyThemeWatcher(folder, OmarchyThemeChanged);
        ReloadOmarchyPalette();
    }

    private void OmarchyThemeChanged()
    {
        if (ReloadOmarchyPalette()) Apply();
    }

    /// <summary>Reads the palette again; true when it differs from the one before.</summary>
    private bool ReloadOmarchyPalette()
    {
        var folder = profile.Environment.IsOmarchy ? profile.Environment.OmarchyCurrentFolder : null;
        var palette = folder is null ? null : OmarchyPalette.TryLoad(folder);
        if (Equals(palette, _omarchyPalette)) return false;
        _omarchyPalette = palette;
        _themePalette = palette is null ? null : ToThemePalette(palette);
        _paletteRevision++;
        if (palette is not null) Serilog.Log.Information("Omarchy theme {Name} loaded ({Mode}).", palette.Name, palette.IsDark ? "dark" : "light");
        return true;
    }

    /// <summary>Adds the palette and the tiling look to <paramref name="input"/>; called by <see cref="Apply"/> only.</summary>
    private ThemeCssInput ApplyDesktop(ThemeCssInput input, bool isDefaultTheme, bool previewing, bool dark, out int paletteRevision)
    {
        var style = profile.EffectiveStyle;
        WindowChrome.Apply(style);
        input = input with { Tiling = style == LinuxWindowStyle.Tiling };

        string? skipped = !profile.Environment.IsOmarchy ? "Omarchy is not detected"
            : !profile.FollowOmarchyTheme ? "Follow the Omarchy theme is off"
            : _themePalette is null || _omarchyPalette is null ? "the Omarchy theme has no readable colors.toml"
            : !isDefaultTheme ? "a Wino theme other than Default is selected"
            : previewing ? "the theme editor shows a preview"
            : _omarchyPalette.IsDark != dark ? $"the palette is {(_omarchyPalette.IsDark ? "dark" : "light")} but the app is {(dark ? "dark" : "light")}"
            : null;
        AppliedPalette = skipped is null ? _themePalette : null;
        DesktopStatus = skipped is null ? "palette applied" : "palette skipped: " + skipped;
        paletteRevision = AppliedPalette is null ? 0 : _paletteRevision;
        if (AppliedPalette is not { } palette) return input;
        _systemAccentHex = ThemeCss.Hex(palette.Accent);
        return input with { Palette = palette, Accent = input.Accent ?? palette.Accent };
    }

    private static ThemePalette ToThemePalette(OmarchyPalette palette)
    {
        WinoRgba? Read(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (palette.Color(key) is { } hex && Parse(hex) is { } color) return color;
            }
            return null;
        }

        var background = Read("background") ?? new WinoRgba(0, 0, 0);
        var foreground = Read("foreground") ?? new WinoRgba(255, 255, 255);
        var raised = Read("lighter_background") ?? Mix(foreground, background, 0.06);
        return new ThemePalette(
            Window: background,
            WindowFg: foreground,
            View: background,
            Headerbar: background,
            Sidebar: Read("dark_background") ?? background,
            Card: raised,
            Popover: raised,
            Dialog: raised,
            Accent: Read("accent", "blue", "color4") ?? new WinoRgba(0x35, 0x84, 0xe4),
            Destructive: Read("red", "color1"),
            Success: Read("green", "color2"),
            Warning: Read("yellow", "color3"));
    }

    private static WinoRgba? Parse(string hex)
        => hex.Length == 4 ? WinoRgba.FromHexString(string.Concat("#", hex[1], hex[1], hex[2], hex[2], hex[3], hex[3])) : WinoRgba.FromHexString(hex);

    private static WinoRgba Mix(WinoRgba top, WinoRgba bottom, double amount)
    {
        static byte Channel(byte a, byte b, double t) => (byte)Math.Clamp((int)Math.Round(b + (a - b) * t), 0, 255);
        return new WinoRgba(Channel(top.R, bottom.R, amount), Channel(top.G, bottom.G, amount), Channel(top.B, bottom.B, amount));
    }
}
