
using Windows.System;
using Windows.UI.Core;

using Wino.Core.Domain.Interfaces;

#if NET8_0
using Microsoft.UI.Xaml;
#else
using Windows.UI.Xaml;
#endif

namespace Wino.Core.UWP.Services
{
    public class KeyPressService : IKeyPressService
    {
        public bool IsCtrlKeyPressed()
            => Window.Current?.CoreWindow?.GetKeyState(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) ?? false;

        public bool IsShiftKeyPressed()
            => Window.Current?.CoreWindow?.GetKeyState(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down) ?? false;
    }
}
