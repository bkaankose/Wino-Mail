using Foundation;

namespace Wino.Platform.MacOS;

/// <summary>Uses the OS-resolved container paths when App Sandbox is enabled.</summary>
public sealed class MacOSPaths
{
    public MacOSPaths(string packagedResourceRoot)
    {
        var support = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path
            ?? throw new InvalidOperationException("Application Support is unavailable.");
        ApplicationDataRoot = Path.Combine(support, MacOSApplicationIdentity.Value);
        TemporaryRoot = Path.Combine(Path.GetTempPath(), MacOSApplicationIdentity.Value);
        PackagedResourceRoot = Path.GetFullPath(packagedResourceRoot);
    }

    public string ApplicationDataRoot { get; }
    public string TemporaryRoot { get; }
    public string PackagedResourceRoot { get; }
    public bool IsVerifiedNewInstallation => !Directory.Exists(ApplicationDataRoot) && !File.Exists(ApplicationDataRoot);

    public void CreateDirectories()
    {
        Directory.CreateDirectory(ApplicationDataRoot);
        Directory.CreateDirectory(TemporaryRoot);
    }
}
