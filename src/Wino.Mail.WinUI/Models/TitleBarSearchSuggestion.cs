namespace Wino.Mail.WinUI.Models;

public sealed class TitleBarSearchSuggestion(string title, string subtitle = "", object? tag = null)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public object? Tag { get; } = tag;
}
