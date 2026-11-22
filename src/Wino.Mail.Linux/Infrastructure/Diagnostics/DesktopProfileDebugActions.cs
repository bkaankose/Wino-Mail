#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Personalization;
using Wino.Platform.Linux.Desktop;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Debug actions of the desktop profile, each with a string parameter: <c>debug-desktop-style</c> (<c>auto</c>,
/// <c>standard</c>, <c>tiling</c>: a runtime override that is not stored), <c>debug-omarchy-source</c> (a folder treated
/// as Omarchy's <c>current</c> folder; empty restores detection) and <c>debug-desktop-report</c> (an output path for the
/// environment, the settings, the palette and why it applies or not, the decoration layout, every window's size against
/// its content's minimum with OVERFLOW where it does not fit, and the theme CSS).
/// </summary>
internal static class DesktopProfileDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, services, "debug-desktop-style", (provider, value) =>
        {
            LinuxWindowStyle? style = value.ToLowerInvariant() switch
            {
                "auto" or "" => null,
                "standard" => LinuxWindowStyle.Standard,
                "tiling" => LinuxWindowStyle.Tiling,
                _ => throw new ArgumentException($"'{value}' is not auto, standard or tiling.")
            };
            provider.GetRequiredService<LinuxDesktopProfile>().DebugSetStyle(style);
        });
        Add(application, services, "debug-omarchy-source", (provider, value)
            => provider.GetRequiredService<LinuxDesktopProfile>().DebugSetOmarchySource(value));
        Add(application, services, "debug-desktop-report", (provider, path) => DebugActions.WriteText(path, Report(provider)));
    }

    private static string Report(IServiceProvider provider)
    {
        var profile = provider.GetRequiredService<LinuxDesktopProfile>();
        var themes = provider.GetRequiredService<INewThemeService>() as GtkThemeService;
        var environment = profile.Environment;
        var text = new StringBuilder()
            .Append("Desktop ").Append(environment.Desktop).AppendLine()
            .Append("DetectedStyle ").Append(environment.DetectedStyle).AppendLine()
            .Append("IsOmarchy ").Append(environment.IsOmarchy).AppendLine()
            .Append("OmarchySource ").Append(environment.OmarchyCurrentFolder ?? "(none)")
            .Append(profile.DebugOmarchySource is null ? string.Empty : " (debug override)").AppendLine()
            .Append("WindowStyleSetting ").Append(profile.WindowStyleIndex switch
            {
                LinuxDesktopProfile.StandardIndex => "Standard",
                LinuxDesktopProfile.TilingIndex => "Tiling",
                _ => "Automatic"
            }).AppendLine()
            .Append("DebugStyleOverride ").Append(profile.DebugStyleOverride?.ToString() ?? "(none)").AppendLine()
            .Append("EffectiveStyle ").Append(profile.EffectiveStyle).AppendLine()
            .Append("FollowOmarchyTheme ").Append(profile.FollowOmarchyTheme).AppendLine()
            .Append("WinoTheme ").Append(WinoThemeCatalog.Find(themes?.CurrentApplicationThemeId ?? WinoThemeCatalog.DefaultThemeId)?.Name ?? "(custom)").AppendLine()
            .Append("StyleManager.Dark ").Append(Adw.StyleManager.GetDefault().Dark).AppendLine()
            .Append("DecorationLayout '").Append(WindowChrome.DecorationLayout).Append('\'').AppendLine()
            .Append("WinoAppearance tiling=").Append(WinoAppearance.IsTiling).Append(" paletteRevision=").Append(WinoAppearance.PaletteRevision)
            .Append(" accent=").Append(WinoAppearance.Accent is { } accent ? ThemeCss.Hex(accent) : "(system)").AppendLine();

        if (themes?.LoadedOmarchyPalette is { } palette)
        {
            text.Append("OmarchyTheme ").Append(palette.Name).Append(palette.IsDark ? " (dark)" : " (light)").AppendLine();
            foreach (var (key, value) in palette.Colors.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                text.Append("  ").Append(key).Append(" = ").Append(value).AppendLine();
        }
        else
        {
            text.AppendLine("OmarchyTheme (none)");
        }
        text.Append("Palette ").Append(themes?.DesktopStatus ?? "(no theme service)").AppendLine();

        text.AppendLine("Windows:");
        var toplevels = Gtk.Window.GetToplevels();
        for (uint index = 0; index < toplevels.GetNItems(); index++)
        {
            if (toplevels.GetObject(index) is not Gtk.Window window || !window.GetVisible()) continue;
            var width = window.GetWidth();
            var height = window.GetHeight();
            text.Append("  '").Append(window.GetTitle()).Append("' ").Append(width).Append('x').Append(height);
            if (window.GetChild() is { } child)
            {
                child.Measure(Gtk.Orientation.Horizontal, -1, out var minimumWidth, out _, out _, out _);
                child.Measure(Gtk.Orientation.Vertical, Math.Max(width, minimumWidth), out var minimumHeight, out _, out _, out _);
                text.Append(" minimum ").Append(minimumWidth).Append('x').Append(minimumHeight);
                if (minimumWidth > width || minimumHeight > height) text.Append(" OVERFLOW");
            }
            text.AppendLine();
            ReportBreakpointBins(text, window);
        }

        return text.AppendLine("Css:").Append(themes?.LastCss ?? string.Empty).ToString();
    }

    // A breakpoint bin's minimum is its size request, so the window's minimum hides content that does not fit in it.
    private static void ReportBreakpointBins(StringBuilder text, Gtk.Widget widget)
    {
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (child is Adw.BreakpointBin bin && bin.GetMapped() && bin.GetChild() is { } content)
            {
                var width = bin.GetWidth();
                var height = bin.GetHeight();
                content.Measure(Gtk.Orientation.Horizontal, -1, out var minimumWidth, out _, out _, out _);
                content.Measure(Gtk.Orientation.Vertical, Math.Max(width, minimumWidth), out var minimumHeight, out _, out _, out _);
                text.Append("    breakpoint bin ").Append(width).Append('x').Append(height)
                    .Append(" content minimum ").Append(minimumWidth).Append('x').Append(minimumHeight)
                    .Append(bin.GetCurrentBreakpoint() is null ? string.Empty : " (narrow)")
                    .Append(minimumWidth > width || minimumHeight > height ? " OVERFLOW" : string.Empty).AppendLine();
                if (minimumWidth > width) ReportWidest(text, content, width);
            }
            ReportBreakpointBins(text, child);
        }
    }

    // The innermost widgets whose minimum width alone exceeds the room: the ones to fix.
    private static void ReportWidest(StringBuilder text, Gtk.Widget widget, int room)
    {
        var deeper = false;
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (!child.GetVisible()) continue;
            child.Measure(Gtk.Orientation.Horizontal, -1, out var minimum, out _, out _, out _);
            if (minimum <= room) continue;
            deeper = true;
            ReportWidest(text, child, room);
        }
        if (deeper) return;
        widget.Measure(Gtk.Orientation.Horizontal, -1, out var own, out _, out _, out _);
        text.Append("      widest ").Append(widget.GetType().Name).Append(" '").Append(widget.GetCssName()).Append("' classes=")
            .Append(string.Join(',', widget.GetCssClasses() ?? [])).Append(" label='").Append(widget is Gtk.Label label ? label.GetText() : string.Empty)
            .Append("' minimum ").Append(own).AppendLine();
    }

    private static void Add(Adw.Application application, Func<IServiceProvider?> services, string name, Action<IServiceProvider, string> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New("s"));
        action.OnActivate += (_, args) =>
        {
            var value = args.Parameter?.GetString(out var _) ?? string.Empty;
            try
            {
                run(services() ?? throw new InvalidOperationException("The app has not started."), value);
                Serilog.Log.Information("Debug action {Name} {Value} done.", name, value);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
        };
        application.AddAction(action);
    }
}
#endif
