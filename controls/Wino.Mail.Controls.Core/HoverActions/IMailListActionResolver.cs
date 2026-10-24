namespace Wino.Mail.Controls.Core.HoverActions;

public readonly record struct HoverActionSet(HoverActionKind Left, HoverActionKind Center, HoverActionKind Right)
{
    public static HoverActionSet None { get; } = new(HoverActionKind.None, HoverActionKind.None, HoverActionKind.None);
}

/// <summary>
/// Picks the hover actions for a row. Hosts supply one when actions depend on the row's
/// account; without it the list applies the same three actions to every row.
/// </summary>
public interface IMailListActionResolver
{
    HoverActionSet ResolveHoverActions(MailListRow row);
}
