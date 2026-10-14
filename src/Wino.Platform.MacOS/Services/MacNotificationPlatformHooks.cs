using AppKit;
using Foundation;
using ImageIO;
using Wino.Platform.Apple.Services;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// Notification pictures through ImageIO. Notifications are built on background threads, where AppKit types such as
/// NSImage throw AppKitThreadAccessException, so this stays on thread-safe CoreGraphics and ImageIO.
/// </summary>
public sealed class MacNotificationImageConverter : INotificationImageConverter
{
    private const string PngTypeIdentifier = "public.png";

    public bool TryWritePng(string sourcePath, string destinationPath)
    {
        using var source = CGImageSource.FromUrl(NSUrl.FromFilename(sourcePath));
        using var image = source?.CreateImage(0, (CGImageOptions?)null);
        if (image is null) return false;
        using var destination = CGImageDestination.Create(NSUrl.FromFilename(destinationPath), PngTypeIdentifier, 1);
        if (destination is null) return false;
        destination.AddImage(image, (CGImageDestinationOptions?)null);
        return destination.Close();
    }
}

/// <summary>The mail unread count on the Dock tile.</summary>
public sealed class MacDockBadgePresenter : IApplicationBadgePresenter
{
    public void ShowBadge(int count)
        => NSApplication.SharedApplication.DockTile.BadgeLabel = count > 0 ? count.ToString() : string.Empty;
}

/// <summary>The macOS alert sounds: the .aiff files in /System/Library/Sounds, previewed with NSSound.</summary>
public sealed class MacNotificationSoundCatalog : INotificationSoundCatalog
{
    private const string SystemSoundsFolder = "/System/Library/Sounds";
    private const string SoundExtension = ".aiff";

    private static readonly Lazy<IReadOnlyList<string>> SystemSoundList = new(LoadSystemSounds);

    /// <summary>Sorted; read once per process.</summary>
    public IReadOnlyList<string> Sounds => SystemSoundList.Value;

    public string? GetSourcePath(string sound)
        => Sounds.Contains(sound, StringComparer.OrdinalIgnoreCase) ? Path.Combine(SystemSoundsFolder, sound + SoundExtension) : null;

    public void Play(string? sound)
    {
        var preview = sound is null ? null : NSSound.FromName(sound);
        if (preview is null)
        {
            AppKitFramework.NSBeep();
            return;
        }
        preview.Stop();
        preview.Play();
    }

    private static IReadOnlyList<string> LoadSystemSounds()
    {
        try
        {
            return Directory.EnumerateFiles(SystemSoundsFolder, "*" + SoundExtension)
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Could not list the macOS alert sounds.");
            return [];
        }
    }
}
