using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// Hardware keyboard shortcuts on iPad use Command. UIKit reports modifier flags only with a key or pointer event,
/// not as global state, so the Shift state is unknown here and reported as not pressed.
/// </summary>
public sealed class IOSShortcutPlatformService : IShortcutPlatformService
{
    public ModifierKeys PrimaryCommandModifier => ModifierKeys.Command;
    public bool IsShiftKeyPressed() => false;
}
