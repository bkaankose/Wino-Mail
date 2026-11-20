using System;
using Windows.Foundation;

namespace Wino.Mail.WinUI.Controls.Calendar;

/// <summary>
/// A right-click on an empty part of the surface. <see cref="Target"/> carries a time only when
/// <see cref="HasTime"/> is set (an hour slot); otherwise it is the day.
/// </summary>
public sealed class CalendarEmptyAreaContextRequestedEventArgs : EventArgs
{
    public CalendarEmptyAreaContextRequestedEventArgs(DateTime target, bool hasTime, Point position)
    {
        Target = target;
        HasTime = hasTime;
        Position = position;
    }

    public DateTime Target { get; }
    public bool HasTime { get; }

    /// <summary>The pointer position relative to the surface.</summary>
    public Point Position { get; }
}
