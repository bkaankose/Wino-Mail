#if WINDOWS
using FluentAssertions;
using Windows.System;
using Wino.Mail.Controls.HotKeyInput;
using Xunit;

namespace Wino.Mail.Controls.Tests.HotKeyInput;

public sealed class WinoHotKeyInputTests
{
    [Fact]
    public void Format_UsesStableModifierOrder()
    {
        WinoHotKeyInput.Format(
                VirtualKey.Space,
                VirtualKeyModifiers.Windows | VirtualKeyModifiers.Shift |
                VirtualKeyModifiers.Menu | VirtualKeyModifiers.Control)
            .Should().Be("Ctrl+Alt+Shift+Win+Space");
    }

    [Fact]
    public void Format_LeavesModifierOnlyCaptureReadable()
    {
        WinoHotKeyInput.Format(VirtualKey.None, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)
            .Should().Be("Ctrl+Shift");
    }
}
#endif
