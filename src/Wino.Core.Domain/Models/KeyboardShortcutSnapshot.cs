using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models;

public sealed record KeyboardShortcutSnapshot(
    Guid Id,
    WinoApplicationMode Mode,
    string Key,
    ModifierKeys ModifierKeys,
    KeyboardShortcutAction Action,
    DateTime CreatedAt);
