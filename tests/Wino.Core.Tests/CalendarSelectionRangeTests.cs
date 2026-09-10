using FluentAssertions;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public class CalendarSelectionRangeTests
{
    [Theory]
    [InlineData(9, 11)]
    [InlineData(11, 9)]
    public void FromCells_IncludesBothEndpointsInEitherDirection(int anchorHour, int currentHour)
    {
        var date = new DateTime(2026, 9, 10);
        var range = CalendarSelectionRange.FromCells(date.AddHours(anchorHour), date.AddHours(currentHour), TimeSpan.FromMinutes(30));

        range.Start.Should().Be(date.AddHours(9));
        range.End.Should().Be(date.AddHours(11.5));
    }

    [Fact]
    public void IntersectDay_SplitsCrossMidnightSelectionWithoutSelectingFollowingDay()
    {
        var date = new DateTime(2026, 9, 10);
        var range = CalendarSelectionRange.FromCells(date.AddDays(1).AddHours(1), date.AddHours(23), TimeSpan.FromMinutes(30));

        range.IntersectDay(DateOnly.FromDateTime(date)).Should().Be(new CalendarSelectionRange(date.AddHours(23), date.AddDays(1)));
        range.IntersectDay(DateOnly.FromDateTime(date.AddDays(1))).Should().Be(new CalendarSelectionRange(date.AddDays(1), date.AddDays(1).AddHours(1.5)));
        range.IntersectDay(DateOnly.FromDateTime(date.AddDays(2))).Should().BeNull();
    }

    [Fact]
    public void FromCells_MonthRangeIncludesLastSelectedDay()
    {
        var date = new DateTime(2026, 9, 10);
        var range = CalendarSelectionRange.FromCells(date.AddDays(2), date, TimeSpan.FromDays(1));

        range.Should().Be(new CalendarSelectionRange(date, date.AddDays(3)));
        range.IntersectDay(DateOnly.FromDateTime(date.AddDays(3))).Should().BeNull();
    }
}
