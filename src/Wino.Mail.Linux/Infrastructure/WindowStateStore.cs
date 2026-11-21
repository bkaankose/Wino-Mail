using System.Text.Json;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Shell;
using Wino.Services;

namespace Wino.Mail.Linux.Infrastructure;

internal readonly record struct WindowPlacement(int Width, int Height, bool IsMaximized);

/// <summary>The window size and the sidebar width, under the head's own keys (macOS keeps <c>WinoShellSidebarWidth</c> the same way).</summary>
internal sealed class WindowStateStore(IConfigurationService configuration)
{
    private const string WidthKey = "LinuxWindowWidth";
    private const string HeightKey = "LinuxWindowHeight";
    private const string MaximizedKey = "LinuxWindowMaximized";
    private const string SidebarWidthKey = "LinuxShellSidebarWidth";

    public static WindowPlacement DefaultPlacement { get; } = new(ShellMetrics.WindowDefaultWidth, ShellMetrics.WindowDefaultHeight, false);

    public static WindowPlacement LoadPlacement(string applicationDataRoot)
    {
        // Never create the data folder here: LinuxPaths.IsVerifiedNewInstallation (the folder does not exist) decides
        // whether a new protection key is provisioned.
        if (!File.Exists(Path.Combine(applicationDataRoot, "preferences.json"))) return DefaultPlacement;
        try
        {
            var values = new JsonFileConfigurationService(applicationDataRoot);
            return new WindowPlacement(
                ClampLength(values.Get(WidthKey, ShellMetrics.WindowDefaultWidth), ShellMetrics.WindowMinimumWidth),
                ClampLength(values.Get(HeightKey, ShellMetrics.WindowDefaultHeight), ShellMetrics.WindowMinimumHeight),
                values.Get(MaximizedKey, false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning(exception, "The window placement could not be read.");
            return DefaultPlacement;
        }
    }

    public void SavePlacement(WindowPlacement placement)
    {
        SetIfChanged(WidthKey, ClampLength(placement.Width, ShellMetrics.WindowMinimumWidth), ShellMetrics.WindowDefaultWidth);
        SetIfChanged(HeightKey, ClampLength(placement.Height, ShellMetrics.WindowMinimumHeight), ShellMetrics.WindowDefaultHeight);
        if (!configuration.Contains(MaximizedKey) || configuration.Get(MaximizedKey, false) != placement.IsMaximized)
            configuration.Set(MaximizedKey, placement.IsMaximized);
    }

    /// <summary>The remembered size of a secondary window kind ("reader", "composer", ...), or null before its first close.</summary>
    public WindowPlacement? ReadKind(string kind)
    {
        var prefix = KindPrefix(kind);
        if (!configuration.Contains(prefix + "Width") || !configuration.Contains(prefix + "Height")) return null;
        return new WindowPlacement(
            Math.Clamp(configuration.Get(prefix + "Width", 0), 1, ShellMetrics.WindowMaximumLength),
            Math.Clamp(configuration.Get(prefix + "Height", 0), 1, ShellMetrics.WindowMaximumLength),
            configuration.Get(prefix + "Maximized", false));
    }

    public void WriteKind(string kind, WindowPlacement placement)
    {
        var prefix = KindPrefix(kind);
        SetIfChanged(prefix + "Width", Math.Clamp(placement.Width, 1, ShellMetrics.WindowMaximumLength), 0);
        SetIfChanged(prefix + "Height", Math.Clamp(placement.Height, 1, ShellMetrics.WindowMaximumLength), 0);
        if (!configuration.Contains(prefix + "Maximized") || configuration.Get(prefix + "Maximized", false) != placement.IsMaximized)
            configuration.Set(prefix + "Maximized", placement.IsMaximized);
    }

    private static string KindPrefix(string kind) => "LinuxWindow." + kind + ".";

    public int SidebarWidth
    {
        get => ClampSidebar(configuration.Get(SidebarWidthKey, ShellMetrics.SidebarDefaultWidth));
        set => SetIfChanged(SidebarWidthKey, ClampSidebar(value), ShellMetrics.SidebarDefaultWidth);
    }

    public static int ClampSidebar(int width) => Math.Clamp(width, ShellMetrics.SidebarMinimumWidth, ShellMetrics.SidebarMaximumWidth);

    private static int ClampLength(int length, int minimum) => Math.Clamp(length, minimum, ShellMetrics.WindowMaximumLength);

    private void SetIfChanged(string key, int value, int defaultValue)
    {
        if (configuration.Contains(key) && configuration.Get(key, defaultValue) == value) return;
        configuration.Set(key, value);
    }
}
