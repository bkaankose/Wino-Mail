using Android.Content;
using Wino.Platform.Android;

namespace Wino.Mail.Android.Infrastructure;

/// <summary>
/// Copies the APK assets under <c>PackagedResources/</c> to <see cref="AndroidPaths.PackagedResourceRoot"/>, where the
/// shared services read them as packaged resources (<c>ms-appx:///</c> paths). APK assets are not files, so the copy is
/// made once per app version: a stamp file holds the version code that was installed.
/// </summary>
public static class PackagedResourceInstaller
{
    private const string AssetRoot = "PackagedResources";
    private const string StampFileName = ".installed-version";

    public static void Install(Context context, AndroidPaths paths, string versionCode)
    {
        var root = paths.PackagedResourceRoot;
        var stamp = Path.Combine(root, StampFileName);
        try
        {
            if (File.Exists(stamp) && File.ReadAllText(stamp) == versionCode) return;
            var assets = context.Assets ?? throw new InvalidOperationException("The app assets are unavailable.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            int copied = Copy(assets, AssetRoot, root);
            File.WriteAllText(stamp, versionCode);
            Serilog.Log.Information("Installed {Count} packaged resources for version {Version}.", copied, versionCode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Java.IO.IOException)
        {
            // Pages that show packaged pictures fall back to their placeholders; the next launch tries again.
            Serilog.Log.Error(exception, "Installing packaged resources failed.");
        }
    }

    /// <summary>Copies the asset folder <paramref name="assetPath"/> recursively; returns the number of files.</summary>
    private static int Copy(global::Android.Content.Res.AssetManager assets, string assetPath, string target)
    {
        var names = assets.List(assetPath) ?? [];
        int count = 0;
        foreach (var name in names)
        {
            var source = $"{assetPath}/{name}";
            var destination = Path.Combine(target, name);
            // AssetManager.List returns children for folders and nothing for files.
            if (assets.List(source) is { Length: > 0 })
            {
                Directory.CreateDirectory(destination);
                count += Copy(assets, source, destination);
                continue;
            }
            using var input = assets.Open(source);
            using var output = File.Create(destination);
            input.CopyTo(output);
            count++;
        }
        return count;
    }
}
