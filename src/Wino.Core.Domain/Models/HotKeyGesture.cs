using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models;

public readonly record struct HotKeyGesture(string Key, ModifierKeys Modifiers)
{
    private static readonly HashSet<string> ModifierKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Control", "LeftControl", "RightControl",
        "Menu", "LeftMenu", "RightMenu",
        "Shift", "LeftShift", "RightShift",
        "LeftWindows", "RightWindows"
    };

    public static HotKeyGesture Default { get; } = new("Space", ModifierKeys.Control | ModifierKeys.Shift);

    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Key) &&
        Modifiers != ModifierKeys.None &&
        (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) == 0 &&
        !ModifierKeyNames.Contains(Key.Trim()) &&
        !string.Equals(Key.Trim(), "F12", StringComparison.OrdinalIgnoreCase);

    public HotKeyGesture Normalize() => new(Key?.Trim() ?? string.Empty,
        Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows));
}
