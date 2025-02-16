using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services;

public class KeyPressService : IKeyPressService
{
    public bool IsCtrlKeyPressed()
        => Window.Current?.CoreWindow?.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) ?? false;

    public bool IsShiftKeyPressed()
        => Window.Current?.CoreWindow?.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down) ?? false;
}
