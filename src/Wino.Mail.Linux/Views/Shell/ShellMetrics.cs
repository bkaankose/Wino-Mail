namespace Wino.Mail.Linux.Views.Shell;

internal static class ShellMetrics
{
    public const int WindowDefaultWidth = 1360;
    public const int WindowDefaultHeight = 860;
    public const int WindowMinimumWidth = 420;
    public const int WindowMinimumHeight = 420;
    public const int WindowMaximumLength = 10000;
    public const int SidebarMinimumWidth = 220;
    public const int SidebarMaximumWidth = 380;
    public const int SidebarDefaultWidth = 280;
    public const int SidebarDockedMinimumShellWidth = 860;
    public const int HeaderCompactMaximumShellWidth = 560;

    /// <summary>The Daily briefing panel (Windows and macOS: 400).</summary>
    public const int TrailingPanelWidth = 400;

    /// <summary>Below this shell width the trailing panel floats over the content: docked, it needs the sidebar, list, reader and its own width.</summary>
    public const int TrailingPanelDockedMinimumShellWidth = 1300;

    public const int ListMinimumWidth = 280;
    public const int ReaderMinimumWidth = 340;
    public const int ResizeHandleWidth = 6;
}
