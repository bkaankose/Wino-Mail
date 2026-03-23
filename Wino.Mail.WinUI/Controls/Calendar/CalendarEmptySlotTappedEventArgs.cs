using System;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Wino.Calendar.Controls;

public sealed class CalendarEmptySlotTappedEventArgs : EventArgs
{
    public CalendarEmptySlotTappedEventArgs(DateTime clickedDate, Point positionerPoint, Size cellSize)
    {
        ClickedDate = clickedDate;
        PositionerPoint = positionerPoint;
        CellSize = cellSize;
    }

    public DateTime ClickedDate { get; }
    public Point PositionerPoint { get; }
    public Size CellSize { get; }
}
