using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Xunit;

namespace Wino.Core.Tests.Models;

public sealed class HotKeyGestureTests
{
    [Fact]
    public void Default_IsCtrlShiftSpace()
    {
        HotKeyGesture.Default.Should().Be(new HotKeyGesture(
            "Space",
            ModifierKeys.Control | ModifierKeys.Shift));
        HotKeyGesture.Default.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("", ModifierKeys.Control)]
    [InlineData("Space", ModifierKeys.None)]
    [InlineData("Control", ModifierKeys.Control)]
    [InlineData("F12", ModifierKeys.Control)]
    public void IsValid_RejectsUnsafeGestures(string key, ModifierKeys modifiers)
    {
        new HotKeyGesture(key, modifiers).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Normalize_TrimsKeyAndRemovesUnknownModifiers()
    {
        var gesture = new HotKeyGesture(" Space ", ModifierKeys.Control | (ModifierKeys)32).Normalize();

        gesture.Should().Be(new HotKeyGesture("Space", ModifierKeys.Control));
    }
}
