using System.ComponentModel;

namespace Wino.Mail.Controls.Core.HoverActions;

public enum HoverActionKind
{
    None,
    Archive,
    Delete,
    ToggleFlag,
    ToggleRead,
    MoveToJunk,
}

public interface IHoverActionItem : INotifyPropertyChanged
{
    bool IsRead { get; }

    bool IsFlagged { get; }
}

public sealed record HoverActionLabels(
    string Archive,
    string Delete,
    string ToggleFlag,
    string ToggleRead,
    string MoveToJunk)
{
    public static HoverActionLabels Empty { get; } = new("Archive", "Delete", "Flag / Unflag", "Read / Unread", "Move to Junk");

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

public sealed class HoverActionInvokedEventArgs(HoverActionKind action, MailListRow row) : EventArgs
{
    public HoverActionKind Action { get; } = action;

    public MailListRow Row { get; } = row;
}

public sealed record HoverActionCommandRequest(HoverActionKind Action, MailListRow Row);

public static class HoverActionConfiguration
{
    public static IReadOnlyList<HoverActionKind> GetVisibleActions(
        HoverActionKind left,
        HoverActionKind center,
        HoverActionKind right)
        => new[] { left, center, right }
            .Where(static action => action != HoverActionKind.None)
            .ToArray();
}
