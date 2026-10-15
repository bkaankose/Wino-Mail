using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Services;

/// <summary>
/// Hardware keyboard shortcuts on Android use Ctrl. Modifier state arrives only with key events, not as global state,
/// so the Shift state is unknown here and reported as not pressed.
/// </summary>
public sealed class AndroidShortcutPlatformService : IShortcutPlatformService
{
    public ModifierKeys PrimaryCommandModifier => ModifierKeys.Control;
    public bool IsShiftKeyPressed() => false;
}
