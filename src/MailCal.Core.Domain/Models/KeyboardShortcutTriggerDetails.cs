using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models;

public class KeyboardShortcutTriggerDetails
{
    public Guid ShortcutId { get; init; }
    public MailCalApplicationMode Mode { get; init; }
    public KeyboardShortcutAction Action { get; init; }
    public string Key { get; init; } = string.Empty;
    public ModifierKeys ModifierKeys { get; init; }
    public KeyboardShortcutInputContext InputContext { get; init; }
    public bool Handled { get; set; }
    public object Sender { get; init; }
    public object Origin { get; init; }
}
