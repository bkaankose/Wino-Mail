namespace Wino.Core.Domain.Enums;

/// <summary>
/// Where the voice bubble opens on the screen. <see cref="BottomRight"/> opens next to the tray icon
/// like the companion flyout; the others use the corners or the center of the work area.
/// </summary>
public enum VoiceBubblePosition
{
    BottomRight = 0,
    BottomLeft = 1,
    TopRight = 2,
    TopLeft = 3,
    Center = 4
}
