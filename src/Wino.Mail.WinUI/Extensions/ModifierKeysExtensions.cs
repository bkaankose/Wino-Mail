using Windows.System;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Extensions;

/// <summary>
/// Extension methods for converting between Windows VirtualKeyModifiers and Domain ModifierKeys.
/// </summary>
public static class ModifierKeysExtensions
{
    /// <summary>
    /// Converts Windows VirtualKeyModifiers to Domain ModifierKeys.
    /// </summary>
    /// <param name="virtualKeyModifiers">The Windows VirtualKeyModifiers to convert.</param>
    /// <returns>The equivalent Domain ModifierKeys.</returns>
    public static ModifierKeys ToDomainModifierKeys(this VirtualKeyModifiers virtualKeyModifiers)
    {
        var modifierKeys = ModifierKeys.None;

        if (virtualKeyModifiers.HasFlag(VirtualKeyModifiers.Control))
            modifierKeys |= ModifierKeys.Control;

        if (virtualKeyModifiers.HasFlag(VirtualKeyModifiers.Menu)) // Alt key
            modifierKeys |= ModifierKeys.Alt;

        if (virtualKeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
            modifierKeys |= ModifierKeys.Shift;

        if (virtualKeyModifiers.HasFlag(VirtualKeyModifiers.Windows))
            modifierKeys |= ModifierKeys.Windows;

        return modifierKeys;
    }

    /// <summary>
    /// Converts Domain ModifierKeys to Windows VirtualKeyModifiers.
    /// </summary>
    /// <param name="modifierKeys">The Domain ModifierKeys to convert.</param>
    /// <returns>The equivalent Windows VirtualKeyModifiers.</returns>
    public static VirtualKeyModifiers ToVirtualKeyModifiers(this ModifierKeys modifierKeys)
    {
        var virtualKeyModifiers = VirtualKeyModifiers.None;

        if (modifierKeys.HasFlag(ModifierKeys.Control))
            virtualKeyModifiers |= VirtualKeyModifiers.Control;

        if (modifierKeys.HasFlag(ModifierKeys.Alt))
            virtualKeyModifiers |= VirtualKeyModifiers.Menu; // Alt key

        if (modifierKeys.HasFlag(ModifierKeys.Shift))
            virtualKeyModifiers |= VirtualKeyModifiers.Shift;

        if (modifierKeys.HasFlag(ModifierKeys.Windows))
            virtualKeyModifiers |= VirtualKeyModifiers.Windows;

        return virtualKeyModifiers;
    }
}