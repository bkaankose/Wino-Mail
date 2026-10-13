using AudioToolbox;
using Foundation;
using UIKit;
using UserNotifications;
using Wino.Platform.Apple.Services;

namespace Wino.Platform.iOS.Services;

/// <summary>Notification pictures through UIImage: decoded from the source file, written as PNG.</summary>
public sealed class IOSNotificationImageConverter : INotificationImageConverter
{
    public bool TryWritePng(string sourcePath, string destinationPath)
    {
        using var image = UIImage.FromFile(sourcePath);
        using var png = image?.AsPNG();
        return png is not null && png.Save(NSUrl.FromFilename(destinationPath), true);
    }
}

/// <summary>The mail unread count on the Home Screen icon (needs the badge notification authorization).</summary>
public sealed class IOSApplicationBadgePresenter : IApplicationBadgePresenter
{
    public void ShowBadge(int count)
        => UNUserNotificationCenter.Current.SetBadgeCount(Math.Max(0, count), error =>
        {
            if (error is not null)
                Serilog.Log.Warning("Could not set the app badge: {Error}", error.LocalizedDescription);
        });
}

/// <summary>
/// iOS has no API to list or copy the system alert sounds, so the catalog is empty and every notification uses
/// the default sound (the macOS sound names stored in preferences map to it). The preview plays the default
/// "Tri-tone" alert (system sound 1007), which follows the ring/silent switch.
/// </summary>
public sealed class IOSNotificationSoundCatalog : INotificationSoundCatalog
{
    private const uint DefaultAlertSoundId = 1007;
    private static readonly Lazy<SystemSound> DefaultAlertSound = new(() => new SystemSound(DefaultAlertSoundId));

    public IReadOnlyList<string> Sounds { get; } = Array.Empty<string>();

    public string? GetSourcePath(string sound) => null;

    public void Play(string? sound) => DefaultAlertSound.Value.PlaySystemSound();
}
