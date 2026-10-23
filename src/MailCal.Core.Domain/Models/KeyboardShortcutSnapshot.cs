using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models;

public sealed record KeyboardShortcutSnapshot(
    Guid Id,
    MailCalApplicationMode Mode,
    string Key,
    ModifierKeys ModifierKeys,
    KeyboardShortcutAction Action,
    DateTime CreatedAt);
