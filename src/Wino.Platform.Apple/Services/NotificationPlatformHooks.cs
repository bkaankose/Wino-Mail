namespace Wino.Platform.Apple.Services;

/// <summary>
/// Converts a sender or contact picture into a PNG file that a notification can attach.
/// The Mac app uses NSImage; iOS uses UIImage.
/// </summary>
public interface INotificationImageConverter
{
    /// <summary>
    /// Writes a PNG copy of <paramref name="sourcePath"/> to <paramref name="destinationPath"/>.
    /// Returns false when the picture cannot be read or encoded. Called off the UI thread.
    /// </summary>
    bool TryWritePng(string sourcePath, string destinationPath);
}

/// <summary>
/// Shows the mail unread count on the app icon: the Dock tile on macOS, the Home Screen badge on iOS.
/// </summary>
public interface IApplicationBadgePresenter
{
    /// <summary>Shows <paramref name="count"/>, or clears the badge when it is 0. Called on the UI thread.</summary>
    void ShowBadge(int count);
}

/// <summary>
/// The alert sounds a notification can use, where their files are, and the settings preview.
/// The Mac app lists /System/Library/Sounds and plays them with NSSound.
/// </summary>
public interface INotificationSoundCatalog
{
    /// <summary>Selectable sound names, sorted. Empty when the platform offers none.</summary>
    IReadOnlyList<string> Sounds { get; }

    /// <summary>
    /// The file of a listed sound, which <see cref="AppleNotificationSounds"/> copies into the app container's
    /// Library/Sounds (notification sounds must come from there). Null when the sound is unknown.
    /// </summary>
    string? GetSourcePath(string sound);

    /// <summary>
    /// Plays a preview of a listed sound, or the platform's default alert sound when <paramref name="sound"/> is null.
    /// Called on the UI thread.
    /// </summary>
    void Play(string? sound);
}
