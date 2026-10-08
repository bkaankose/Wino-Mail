namespace Wino.Calendar.Controls;

public sealed class HeaderTextLayout
{
    public HeaderTextLayout(string text, double width)
    {
        Text = text;
        Width = width;
    }

    public string Text { get; set; }
    public double Width { get; set; }
}
