namespace Wino.Core.Domain.Models.Calendar;

public class DayHeaderRenderModel
{
    public DayHeaderRenderModel(string dayHeader, double hourHeight)
    {
        DayHeader = dayHeader;
        HourHeight = hourHeight;
    }

    public string DayHeader { get; }
    public double HourHeight { get; }
}
