using Foundation;

namespace Wino.Platform.Apple;

/// <summary>Uses the OS-resolved container paths when App Sandbox is enabled.</summary>
public sealed class ApplePaths
{
    public ApplePaths(AppleApplicationIdentity identity, string packagedResourceRoot)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var support = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.ApplicationSupportDirectory, NSSearchPathDomain.User)[0].Path
            ?? throw new InvalidOperationException("Application Support is unavailable.");
        ApplicationDataRoot = Path.Combine(support, identity.Value);
        TemporaryRoot = Path.Combine(Path.GetTempPath(), identity.Value);
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
