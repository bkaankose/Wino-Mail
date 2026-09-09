using FluentAssertions;
using Xunit;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Core.Tests;

public class OverlappedCalendarLayoutTests
{
    [Fact]
    public void NestedEvents_CascadeAndReuseDepthAfterInnerEventEnds()
    {
        var result = Calculate((0, 100), (10, 20), (15, 18), (20, 30));

        result.Select(item => item.Left).Should().Equal(0, 24, 48, 24);
        result.Should().OnlyContain(item => item.Left + item.Width == 300);
    }

    [Fact]
    public void OverlapChain_UsesDeepestActiveEventInsteadOfReusingLeftColumn()
    {
        var result = Calculate((0, 20), (10, 30), (20, 40));

        result.Select(item => item.Left).Should().Equal(0, 24, 48);
    }

    [Fact]
    public void TouchingAndIsolatedEvents_ResetToFullWidth()
    {
        var result = Calculate((0, 10), (10, 20), (30, 40));

        result.Should().OnlyContain(item => item.Left == 0 && item.Width == 300);
    }

    [Fact]
    public void EqualStarts_DrawLongerFirstThenStableIdRegardlessOfInputOrder()
    {
        CalendarOverlapInterval[] intervals =
        [
            new(0, Guid.Parse("00000000-0000-0000-0000-000000000003"), 0, 10),
            new(1, Guid.Parse("00000000-0000-0000-0000-000000000002"), 0, 20),
            new(2, Guid.Parse("00000000-0000-0000-0000-000000000001"), 0, 20)
        ];

        var result = OverlappedCalendarLayout.Calculate(intervals, 300);

        result.Select(item => item.SourceIndex).Should().Equal(2, 1, 0);
        OverlappedCalendarLayout.Calculate(intervals.Reverse(), 300).Should().Equal(result);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(80)]
    [InlineData(1)]
    [InlineData(0)]
    public void DenseGroups_StayWithinDayAndPreserveSixtyPercentWidth(double width)
    {
        var intervals = Enumerable.Range(0, 100)
            .Select(index => new CalendarOverlapInterval(index, Guid.Empty, index, 200));

        var result = OverlappedCalendarLayout.Calculate(intervals, width);

        result.Should().HaveCount(100);
        result.Should().OnlyContain(item => item.Left >= 0 && item.Width >= width * 0.6 - 0.000001);
        result.Should().OnlyContain(item => Math.Abs(item.Left + item.Width - width) < 0.000001);
        if (width > 0)
            result.Select(item => item.Left).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void IndependentGroups_DoNotShareDenseGroupIndent()
    {
        var intervals = Enumerable.Range(0, 20)
            .Select(index => new CalendarOverlapInterval(index, Guid.Empty, 0, 10))
            .Concat([new(20, Guid.Empty, 20, 40), new(21, Guid.Empty, 30, 40)]);

        var result = OverlappedCalendarLayout.Calculate(intervals, 300);

        result[^2].Left.Should().Be(0);
        result[^1].Left.Should().Be(24);
    }

    [Fact]
    public void MidnightClippedSegments_AreIndependentOnEachDay()
    {
        var firstDay = Calculate((1380, 1440), (1410, 1440));
        var secondDay = Calculate((0, 60), (60, 90));

        firstDay.Select(item => item.Left).Should().Equal(0, 24);
        secondDay.Select(item => item.Left).Should().Equal(0, 0);
    }

    [Fact]
    public void EmptyAndInvalidIntervals_ProduceNoCards()
    {
        OverlappedCalendarLayout.Calculate([], 300).Should().BeEmpty();
        Calculate((10, 10), (20, 10), (double.NaN, 50)).Should().BeEmpty();
    }

    private static IReadOnlyList<CalendarOverlapPlacement> Calculate(params (double Start, double End)[] times)
        => OverlappedCalendarLayout.Calculate(times.Select((time, index) =>
            new CalendarOverlapInterval(index, Guid.Empty, time.Start, time.End)), 300);
}
