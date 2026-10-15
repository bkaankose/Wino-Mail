using Android.Content;

namespace Wino.Platform.Android;

/// <summary>
/// The app's folders. Data, the database, logs (the shared <c>Logs</c> folder below the data root), secure items and
/// the wrapped protection key live under <c>NoBackupFilesDir</c>; temporary files and copies shared with other apps
/// under <c>CacheDir</c>. Every folder is private to the app and removed when it is uninstalled.
/// </summary>
public sealed class AndroidPaths
{
    /// <summary>The cache folder that <c>Resources/xml/wino_file_paths.xml</c> shares through the FileProvider.</summary>
    public const string SharedFilesFolderName = "shared-files";

    public AndroidPaths(string filesDirectory, string cacheDirectory, string noBackupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(noBackupDirectory);
        ApplicationDataRoot = Path.Combine(noBackupDirectory, "data");
        SecureItemsRoot = Path.Combine(noBackupDirectory, "secure");
        ProtectionKeyPath = Path.Combine(noBackupDirectory, "protection", "wino-protection-v1.key");
        TemporaryRoot = Path.Combine(cacheDirectory, "temp");
        SharedFilesRoot = Path.Combine(cacheDirectory, SharedFilesFolderName);
        PackagedResourceRoot = Path.Combine(filesDirectory, "PackagedResources");
    }

    /// <summary>The app's folders from <paramref name="context"/>.</summary>
    public static AndroidPaths FromContext(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AndroidPaths(
            context.FilesDir?.AbsolutePath ?? throw new InvalidOperationException("The files folder is unavailable."),
            context.CacheDir?.AbsolutePath ?? throw new InvalidOperationException("The cache folder is unavailable."),
            context.NoBackupFilesDir?.AbsolutePath ?? throw new InvalidOperationException("The no-backup folder is unavailable."));
    }

    /// <summary><c>IApplicationConfiguration.ApplicationDataFolderPath</c>: database, preferences, logs, pictures.</summary>
    public string ApplicationDataRoot { get; }

    /// <summary>Protected secure item files (<see cref="Wino.Services.Security.ProtectedFileSecureItemStore"/>).</summary>
    public string SecureItemsRoot { get; }

    /// <summary>The protection key, wrapped by the Android Keystore key.</summary>
    public string ProtectionKeyPath { get; }

    public string TemporaryRoot { get; }

    /// <summary>Copies of files opened in other apps; the only folder the FileProvider exposes.</summary>
    public string SharedFilesRoot { get; }

    /// <summary>
    /// Files the shared services read as packaged resources (<c>ms-appx:///</c> paths). APK assets are not files, so the
    /// head copies the assets those services need here.
    /// </summary>
    public string PackagedResourceRoot { get; }

    /// <summary>True when no data, secure item or protection key exists yet: the only time a new protection key is created.</summary>
    public bool IsVerifiedNewInstallation
        => !Exists(ApplicationDataRoot) && !Exists(SecureItemsRoot) && !Exists(ProtectionKeyPath);

    public void CreateDirectories()
    {
        Directory.CreateDirectory(ApplicationDataRoot);
        Directory.CreateDirectory(SecureItemsRoot);
        Directory.CreateDirectory(TemporaryRoot);
        Directory.CreateDirectory(SharedFilesRoot);
        Directory.CreateDirectory(PackagedResourceRoot);
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);
}
