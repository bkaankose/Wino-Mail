namespace Wino.Core.Domain.Enums;

/// <summary>
/// The miniature Wino surface a theme color is previewed on. Each scene draws only the
/// area the matching resource key actually paints in the application.
/// </summary>
public enum ThemeSurfaceScene
{
    Window,
    Navigation,
    Workspace,
    MailListHeader,
    ReadingPane,
    CalendarGrid
}
