namespace Wino.Mail.Controls.Core.HoverActions;

public sealed record HoverActionLabels(
    string Archive,
    string Delete,
    string ToggleFlag,
    string ToggleRead,
    string MoveToJunk)
{
    public static HoverActionLabels Default { get; } =
        new("Archive", "Delete", "Flag / Unflag", "Read / Unread", "Move to Junk");

    public string GetLabel(HoverActionKind action) => action switch
    {
        HoverActionKind.Archive => Archive,
        HoverActionKind.Delete => Delete,
        HoverActionKind.ToggleFlag => ToggleFlag,
        HoverActionKind.ToggleRead => ToggleRead,
        HoverActionKind.MoveToJunk => MoveToJunk,
        _ => string.Empty,
    };
}
