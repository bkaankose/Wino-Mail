using Wino.Core.Domain.Interfaces;

namespace Wino.BackgroundService.Services;

/// <summary>
/// There is no keyboard state in the companion process. Modifier-key gestures
/// (e.g. Shift+Delete upgrading a soft delete to a hard delete) are a UI concern and
/// must be resolved by the UI process before the request crosses the pipe.
/// </summary>
public sealed class HeadlessKeyPressService : IKeyPressService
{
    public bool IsCtrlKeyPressed() => false;

    public bool IsShiftKeyPressed() => false;
}
