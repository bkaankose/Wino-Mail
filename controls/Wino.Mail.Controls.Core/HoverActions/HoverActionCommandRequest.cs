namespace Wino.Mail.Controls.Core.HoverActions;

public sealed record HoverActionCommandRequest(
    HoverActionKind Action,
    MailListRow Row);
