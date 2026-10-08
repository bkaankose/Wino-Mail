using System;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Wino.Calendar.Controls;

public sealed class CalendarEmptySlotTappedEventArgs : EventArgs
{
    public CalendarEmptySlotTappedEventArgs(DateTime clickedDate, Point anchorPoint, Size cellSize, DateTime? endDate = null)
    {
        ClickedDate = clickedDate;
        EndDate = endDate;
        AnchorPoint = anchorPoint;
        CellSize = cellSize;
    }

    public DateTime? EndDate { get; }
    public DateTime ClickedDate { get; }
    public Point AnchorPoint { get; }
    public Size CellSize { get; }
}
