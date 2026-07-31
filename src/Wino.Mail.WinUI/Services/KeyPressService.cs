using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.WinUI.Services;

public class KeyPressService : IKeyPressService
{
    public bool IsCtrlKeyPressed()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    public bool IsShiftKeyPressed()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
}
