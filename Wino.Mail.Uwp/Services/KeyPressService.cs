using Windows.System;
using Windows.UI.Core;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Uwp.Services;

public class KeyPressService : IKeyPressService
{
    public bool IsCtrlKeyPressed()
        => CoreWindow.GetForCurrentThread().GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    public bool IsShiftKeyPressed()
        => CoreWindow.GetForCurrentThread().GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
}
