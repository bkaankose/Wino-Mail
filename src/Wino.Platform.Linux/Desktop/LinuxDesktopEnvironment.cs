namespace Wino.Platform.Linux.Desktop;

public enum LinuxWindowStyle
{
    Standard,
    Tiling
}

/// <summary>
/// What the session's environment variables and state files say about the desktop: whether a tiling compositor runs
/// it and whether Omarchy manages its theme. Read once at start; no GTK and no D-Bus.
/// </summary>
/// <param name="OmarchyCurrentFolder">Omarchy's <c>current</c> folder (it holds <c>theme/</c> and <c>theme.name</c>), or null.</param>
public sealed record LinuxDesktopEnvironment(LinuxWindowStyle DetectedStyle, bool IsOmarchy, string? OmarchyCurrentFolder, string Desktop)
{
    private static readonly string[] TilingDesktops = ["hyprland", "sway", "niri", "river", "i3"];
    private static readonly string[] TilingSockets = ["HYPRLAND_INSTANCE_SIGNATURE", "SWAYSOCK", "NIRI_SOCKET"];

    public static LinuxDesktopEnvironment Detect()
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        var tiling = desktop.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(name => TilingDesktops.Contains(name, StringComparer.OrdinalIgnoreCase))
            || TilingSockets.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));
        var omarchy = FindOmarchyCurrentFolder();
        return new LinuxDesktopEnvironment(tiling ? LinuxWindowStyle.Tiling : LinuxWindowStyle.Standard, omarchy is not null, omarchy, desktop);
    }

    private static string? FindOmarchyCurrentFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(state) || !Path.IsPathRooted(state)) state = Path.Combine(home, ".local", "state");
        string[] candidates = [Path.Combine(state, "omarchy", "current"), Path.Combine(home, ".config", "omarchy", "current")];
        foreach (var current in candidates)
        {
            if (Directory.Exists(Path.Combine(current, "theme"))) return current;
        }
        return null;
    }
}
