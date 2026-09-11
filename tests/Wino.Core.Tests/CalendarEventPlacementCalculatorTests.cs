using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public class CalendarEventPlacementCalculatorTests
{
    [Fact]
    public void Stacked_PreservesExistingColumnWidthsAndSpacing()
    {
        var result = Place(CalendarEventDisplayMode.Stacked, (0, 120), (30, 90));

        result.Select(item => item.Left).Should().Equal(2, 182);
        result.Select(item => item.Width).Should().Equal(166, 166);
    }

    [Fact]
    public void Cascade_PreservesExistingOffsets()
    {
        var result = Place(CalendarEventDisplayMode.Overlapped, (0, 120), (30, 90));

        result.Select(item => item.Left).Should().Equal(2, 26);
        result.Select(item => item.Width).Should().Equal(346, 322);
    }

    [Fact]
    public void LimitedOverlap_ReusesLanesAndPaintsRightLaneLast()
    {
        var result = Place(CalendarEventDisplayMode.LimitedOverlap, (0, 60), (30, 120), (60, 90));

        result.Select(item => item.SourceIndex).Should().Equal(0, 2, 1);
        result[0].Left.Should().Be(result[1].Left);
        result[0].Width.Should().BeApproximately(173 * 1.3, 0.001);
        result[2].Left.Should().Be(175);
        result[2].Width.Should().Be(173);
    }

    [Fact]
    public void ProtectTitles_SeparatesNearStartsButLetsLaterBodiesShareWidth()
    {
        var result = Place(CalendarEventDisplayMode.ProtectTitles, (0, 180), (0, 120), (60, 150));

        result[0].Left.Should().NotBe(result[1].Left);
        result[0].Left.Should().Be(result[2].Left);
        result[2].Width.Should().BeGreaterThan(result[0].Width);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(120)]
    public void ProtectTitles_ReservesThirtyTwoPixelsAtEveryHourHeight(double hourHeight)
    {
        CalendarOverlapInterval[] events = [new(0, Guid.Empty, 0, 180), new(1, Guid.Empty, 20, 160)];
        var result = CalendarEventPlacementCalculator.Calculate(events, 360, hourHeight, CalendarEventDisplayMode.ProtectTitles);

        if (20 * hourHeight / 60 < CalendarEventPlacementCalculator.ProtectedTitleHeight)
            result[1].Left.Should().BeGreaterThan(result[0].Left);
        else
            result[1].Left.Should().Be(result[0].Left);
    }

    [Theory]
    [InlineData(CalendarEventDisplayMode.Stacked)]
    [InlineData(CalendarEventDisplayMode.Overlapped)]
    [InlineData(CalendarEventDisplayMode.LimitedOverlap)]
    [InlineData(CalendarEventDisplayMode.ProtectTitles)]
    public void DenseEvents_StayWithinBoundsAndKeepEveryEvent(CalendarEventDisplayMode mode)
    {
        var events = Enumerable.Range(0, 30).Select(i => new CalendarOverlapInterval(i, Guid.Empty, 0, 180)).ToArray();
        foreach (var width in new[] { 0d, 10d, 80d, 360d })
        {
            var result = CalendarEventPlacementCalculator.Calculate(events, width, 60, mode);
            result.Should().HaveCount(events.Length);
            result.Should().OnlyContain(item => item.Width >= 0 && item.Left >= 0 && item.Left + item.Width <= width + 0.001);
        }
    }

    private static IReadOnlyList<CalendarOverlapPlacement> Place(CalendarEventDisplayMode mode, params (double Start, double End)[] times)
        => CalendarEventPlacementCalculator.Calculate(times.Select((time, i) => new CalendarOverlapInterval(i, Guid.Empty, time.Start, time.End)), 360, 60, mode);
}
