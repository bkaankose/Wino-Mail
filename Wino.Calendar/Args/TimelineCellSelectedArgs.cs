using System;
using Windows.Foundation;

namespace Wino.Calendar.Args
{
    /// <summary>
    /// When a new timeline cell is selected.
    /// </summary>
    public class TimelineCellSelectedArgs : EventArgs
    {
        public TimelineCellSelectedArgs(DateTime clickedDate, Point canvasPoint, Point positionerPoint, Size cellSize)
        {
            ClickedDate = clickedDate;
            CanvasPoint = canvasPoint;
            PositionerPoint = positionerPoint;
            CellSize = cellSize;
        }

        /// <summary>
        /// Clicked date and time information for the cell.
        /// </summary>
        public DateTime ClickedDate { get; set; }

        /// <summary>
        /// Position relative to the cell drawing part of the canvas.
        /// Used to detect clicked cell from the position.
        /// </summary>
        public Point CanvasPoint { get; }

        /// <summary>
        /// Position relative to the main root positioner element of the drawing canvas.
        /// Used to show the create event dialog teaching tip in correct position.
        /// </summary>
        public Point PositionerPoint { get; }

        /// <summary>
        /// Size of the cell.
        /// </summary>
        public Size CellSize { get; }
    }
}
