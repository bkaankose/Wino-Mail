using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

public sealed class MacShortcutPlatformService : IShortcutPlatformService
{
    public ModifierKeys PrimaryCommandModifier => ModifierKeys.Command;
    public bool IsShiftKeyPressed() => (NSEvent.CurrentModifierFlags & NSEventModifierMask.ShiftKeyMask) != 0;
}
