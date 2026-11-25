namespace Wino.Platform.Linux;

/// <summary>
/// The app's folders by the XDG Base Directory specification: data, the database, logs and protected secure items
/// below <c>$XDG_DATA_HOME/wino-mail</c>, temporary files below <c>$XDG_CACHE_HOME/wino-mail</c>.
/// </summary>
public sealed class LinuxPaths
{
    public const string FolderName = "wino-mail";

    public LinuxPaths(string dataHome, string cacheHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataHome);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheHome);
        var root = Path.Combine(dataHome, FolderName);
        ApplicationDataRoot = Path.Combine(root, "data");
        SecureItemsRoot = Path.Combine(root, "secure");
        TemporaryRoot = Path.Combine(cacheHome, FolderName, "temp");
    }

    public static LinuxPaths FromEnvironment()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new LinuxPaths(AbsoluteOr("XDG_DATA_HOME", Path.Combine(home, ".local", "share")),
            AbsoluteOr("XDG_CACHE_HOME", Path.Combine(home, ".cache")));
    }

    public string ApplicationDataRoot { get; }

    /// <summary>Protected secure item files (<see cref="Wino.Services.Security.ProtectedFileSecureItemStore"/>).</summary>
    public string SecureItemsRoot { get; }

    public string TemporaryRoot { get; }

    /// <summary>True when no data and no secure item exists yet: the only time a new protection key is created.</summary>
    public bool IsVerifiedNewInstallation => !Directory.Exists(ApplicationDataRoot) && !Directory.Exists(SecureItemsRoot);

    public void CreateDirectories()
    {
        Directory.CreateDirectory(ApplicationDataRoot);
        // The envelopes are encrypted, but the folder stays private to the user as well.
        Directory.CreateDirectory(SecureItemsRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(TemporaryRoot);
    }

    // The specification ignores a relative path in these variables.
    private static string AbsoluteOr(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrEmpty(value) && Path.IsPathRooted(value) ? value : fallback;
    }
}
