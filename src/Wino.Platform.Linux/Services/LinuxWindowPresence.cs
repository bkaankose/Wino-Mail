namespace Wino.Platform.Linux.Services;

/// <summary>Whether the application has a window to show sign-in UI over; set by the head, read on any thread.</summary>
public sealed class LinuxWindowPresence
{
    private volatile bool _hasWindow;

    public bool HasWindow
    {
        get => _hasWindow;
        set => _hasWindow = value;
    }
}
