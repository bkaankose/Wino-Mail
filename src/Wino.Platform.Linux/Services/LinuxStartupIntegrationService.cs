using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Linux.Native;

namespace Wino.Platform.Linux.Services;

/// <summary>
/// Launch at sign-in by the freedesktop autostart specification: an entry in <c>$XDG_CONFIG_HOME/autostart</c> that
/// starts the app with <c>--background</c>. Inside a sandbox the Background portal writes it instead, and the answer
/// is remembered in the app's own data, because the sandbox cannot read the host's autostart folder.
/// </summary>
public sealed class LinuxStartupIntegrationService(LinuxPaths paths, IDispatcher dispatcher) : IStartupIntegrationService
{
    public const string BackgroundOption = "--background";

    private const string EntryName = "app.winomail.Mail.desktop";

    public static string AutostartFolder
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = !string.IsNullOrEmpty(config) && Path.IsPathRooted(config)
                ? config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "autostart");
        }
    }

    public static string AutostartEntryPath => Path.Combine(AutostartFolder, EntryName);

    private string PortalMarkerPath => Path.Combine(paths.ApplicationDataRoot, "autostart-portal");

    public Task<StartupBehaviorResult> GetCurrentBehaviorAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (BackgroundPortal.IsSandboxed)
                return Task.FromResult(File.Exists(PortalMarkerPath) ? StartupBehaviorResult.Enabled : StartupBehaviorResult.Disabled);
            return Task.FromResult(ReadStatus(AutostartEntryPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(exception, "Could not read the autostart entry.");
            return Task.FromResult(StartupBehaviorResult.Fatal);
        }
    }

    public async Task<StartupBehaviorResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BackgroundPortal.IsSandboxed) return await SetThroughPortalAsync(isEnabled, cancellationToken);
        try
        {
            var path = AutostartEntryPath;
            if (!isEnabled)
            {
                File.Delete(path);
                return StartupBehaviorResult.Disabled;
            }
            Directory.CreateDirectory(AutostartFolder);
            var temporary = path + ".partial";
            File.WriteAllText(temporary, CreateEntry(ExecutableCommand()), new UTF8Encoding(false));
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temporary, path, overwrite: true);
            return ReadStatus(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(exception, "Could not change the autostart entry.");
            return StartupBehaviorResult.Fatal;
        }
    }

    /// <summary>Missing: disabled; turned off by the user in another tool (<c>Hidden</c>, GNOME's enabled key): disabled by the user.</summary>
    public static StartupBehaviorResult ReadStatus(string path)
    {
        if (!File.Exists(path)) return StartupBehaviorResult.Disabled;
        var inEntry = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) inEntry = line == "[Desktop Entry]";
            else if (inEntry && (IsKey(line, "Hidden", "true") || IsKey(line, "X-GNOME-Autostart-enabled", "false")))
                return StartupBehaviorResult.DisabledByUser;
        }
        return StartupBehaviorResult.Enabled;
    }

    public static string CreateEntry(string command) => string.Join('\n',
        "[Desktop Entry]",
        "Type=Application",
        "Name=Wino Mail",
        "Comment=Mail, calendar, contacts and To Do",
        $"Exec={command} {BackgroundOption}",
        "Icon=app.winomail.Mail",
        "Terminal=false",
        "NoDisplay=true",
        "X-GNOME-Autostart-enabled=true",
        string.Empty);

    /// <summary>The installed entry's program when the desktop entry is installed, else this executable, quoted for <c>Exec</c>.</summary>
    private static string ExecutableCommand()
    {
        if (InstalledEntryProgram() is { } installed) return installed;
        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "wino-mail");
        return Quote(executable);
    }

    private static string? InstalledEntryProgram()
    {
        foreach (var folder in DataFolders())
        {
            var entry = Path.Combine(folder, "applications", EntryName);
            if (!File.Exists(entry)) continue;
            foreach (var raw in File.ReadLines(entry))
            {
                var line = raw.Trim();
                if (line.StartsWith('[') && line != "[Desktop Entry]") break;
                if (!line.StartsWith("Exec=", StringComparison.Ordinal)) continue;
                var exec = line["Exec=".Length..];
                var program = exec.StartsWith('"') ? exec[..(exec.IndexOf('"', 1) is var end and > 0 ? end + 1 : exec.Length)] : exec.Split(' ')[0];
                return program.Length > 0 ? program : null;
            }
        }
        return null;
    }

    private static IEnumerable<string> DataFolders()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        yield return !string.IsNullOrEmpty(home) && Path.IsPathRooted(home)
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var dir in (string.IsNullOrEmpty(dirs) ? "/usr/local/share:/usr/share" : dirs).Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (Path.IsPathRooted(dir)) yield return dir;
    }

    // Desktop entry Exec quoting: inside double quotes ", `, $ and \ take a backslash, and the value escapes \ once more.
    private static string Quote(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var character in path)
        {
            if (character is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(character);
        }
        return quoted.Append('"').ToString().Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    private static bool IsKey(string line, string key, string value)
    {
        var separator = line.IndexOf('=');
        return separator > 0 && line[..separator].Trim() == key && string.Equals(line[(separator + 1)..].Trim(), value, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<StartupBehaviorResult> SetThroughPortalAsync(bool isEnabled, CancellationToken cancellationToken)
    {
        var answer = await BackgroundPortal.RequestAsync(dispatcher, isEnabled, ["wino-mail", BackgroundOption], cancellationToken);
        if (answer is not { } autostart) return StartupBehaviorResult.Fatal;
        try
        {
            if (autostart) File.WriteAllText(PortalMarkerPath, string.Empty);
            else File.Delete(PortalMarkerPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(exception, "Could not remember the Background portal answer.");
        }
        if (isEnabled && !autostart) return StartupBehaviorResult.DisabledByUser;
        return autostart ? StartupBehaviorResult.Enabled : StartupBehaviorResult.Disabled;
    }
}
