using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models;

public class KeyboardShortcutTriggerDetails
{
    public Guid ShortcutId { get; init; }
    public WinoApplicationMode Mode { get; init; }
    public KeyboardShortcutAction Action { get; init; }
    public string Key { get; init; } = string.Empty;
    public ModifierKeys ModifierKeys { get; init; }
    public bool Handled { get; set; }
    public object Sender { get; init; }
    public object Origin { get; init; }
}
