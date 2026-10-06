using System.Windows.Input;
using Wino.Mail.Controls.Core.HoverActions;

namespace Wino.Mail.Controls.HoverActions;

public sealed class HoverActionButtonItem(
    HoverActionKind action,
    string label,
    string glyph,
    HoverActionButtonSize buttonSize,
    ICommand? command,
    HoverActionCommandRequest commandParameter)
{
    public HoverActionKind Action { get; } = action;

    public string Label { get; } = label;

    public string Glyph { get; } = glyph;

    public HoverActionButtonSize ButtonSize { get; } = buttonSize;

    public ICommand? Command { get; } = command;

    public HoverActionCommandRequest CommandParameter { get; } = commandParameter;

    public string AutomationId => $"HoverAction{Action}Button";
}
