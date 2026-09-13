using Wino.Core.Domain.Enums;
using Wino.Mail.WinUI.ThirdParty.DesktopFlyouts;
using Xunit;

namespace Wino.Core.Tests.Companion;

public sealed class CompanionFlyoutPlacementCalculatorTests
{
    private static readonly CompanionRect WorkArea = new(-1920, -200, 1920, 1080);
    [Fact]
    public void Calculate_AnchorsToTrayIconOnEveryEdge()
    {
        AssertIconAnchor(WindowsTaskbarPosition.Left, new CompanionRect(-1900, 300, 24, 24), -1868, 112);
        AssertIconAnchor(WindowsTaskbarPosition.Top, new CompanionRect(-1000, -190, 24, 24), -1188, -158);
        AssertIconAnchor(WindowsTaskbarPosition.Right, new CompanionRect(-220, 300, 24, 24), -628, 112);
        AssertIconAnchor(WindowsTaskbarPosition.Bottom, new CompanionRect(-1000, 850, 24, 24), -1188, 442);
    }

    private static void AssertIconAnchor(
        WindowsTaskbarPosition position,
        CompanionRect icon,
        int expectedX,
        int expectedY)
    {
        var result = CompanionFlyoutPlacementCalculator.Calculate(
            WorkArea,
            icon,
            position,
            flyoutWidth: 400,
            flyoutHeight: 400,
            gap: 8);

        Assert.Equal(new CompanionRect(expectedX, expectedY, 400, 400), result);
    }

    [Theory]
    [InlineData(WindowsTaskbarPosition.Left, -1912, 140)]
    [InlineData(WindowsTaskbarPosition.Top, -408, -192)]
    [InlineData(WindowsTaskbarPosition.Right, -408, 140)]
    [InlineData(WindowsTaskbarPosition.Bottom, -408, 472)]
    public void Calculate_UsesOrientationFallback(
        WindowsTaskbarPosition position,
        int expectedX,
        int expectedY)
    {
        var result = CompanionFlyoutPlacementCalculator.Calculate(
            WorkArea,
            iconRect: null,
            position,
            flyoutWidth: 400,
            flyoutHeight: 400,
            gap: 8);

        Assert.Equal(new CompanionRect(expectedX, expectedY, 400, 400), result);
    }

    [Fact]
    public void Calculate_ClampsCompleteFlyoutToWorkArea()
    {
        var result = CompanionFlyoutPlacementCalculator.Calculate(
            new CompanionRect(-1000, -500, 800, 600),
            new CompanionRect(-1200, -700, 20, 20),
            WindowsTaskbarPosition.Bottom,
            flyoutWidth: 500,
            flyoutHeight: 400,
            gap: 8);

        Assert.Equal(new CompanionRect(-1000, -500, 500, 400), result);
    }
}
