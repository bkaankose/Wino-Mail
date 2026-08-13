using System;

namespace Wino.Core.Domain.Enums;

/// <summary>
/// Defines keyboard modifier keys that can be used in keyboard shortcuts.
/// </summary>
[Flags]
public enum ModifierKeys
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}