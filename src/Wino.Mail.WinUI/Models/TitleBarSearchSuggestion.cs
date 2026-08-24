namespace Wino.Mail.WinUI.Models;

public sealed class TitleBarSearchSuggestion(string title, string subtitle = "", object? tag = null, object? contactPicture = null)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public object? Tag { get; } = tag;
    public object? ContactPicture { get; } = contactPicture;

    public override string ToString() => Title;
}
