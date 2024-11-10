namespace Wino.Calendar.Models
{
    public struct CalendarItemMeasurement
    {
        // Where to start?
        public double Left { get; set; }

        // Extend until where?
        public double Right { get; set; }

        public CalendarItemMeasurement(double left, double right)
        {
            Left = left;
            Right = right;
        }
    }
}
