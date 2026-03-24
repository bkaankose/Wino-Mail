using System.Linq;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public class CalendarRangeTextFormatterTests
{
    private static readonly CalendarRangeTextFormatter Formatter = new();
    private static readonly TestDateContextProvider DateContextProvider = new("en-US", new DateOnly(2026, 3, 24));

    [Fact]
    public void Format_ReturnsMonthDay_ForSingleDate()
    {
        var range = CreateRange(
            CalendarDisplayType.Day,
            anchorDate: new DateOnly(2026, 3, 6),
            startDate: new DateOnly(2026, 3, 6),
            endDate: new DateOnly(2026, 3, 6));

        Formatter.Format(range, DateContextProvider).Should().Be("March 6");
    }

    [Fact]
    public void Format_ReturnsFullRange_ForDatesInSameMonth()
    {
        var range = CreateRange(
            CalendarDisplayType.Week,
            anchorDate: new DateOnly(2026, 3, 6),
            startDate: new DateOnly(2026, 3, 3),
            endDate: new DateOnly(2026, 3, 10));

        Formatter.Format(range, DateContextProvider).Should().Be("March 3 - March 10");
    }

    [Fact]
    public void Format_ReturnsFullRange_ForDatesInDifferentMonths()
    {
        var range = CreateRange(
            CalendarDisplayType.Week,
            anchorDate: new DateOnly(2026, 4, 2),
            startDate: new DateOnly(2026, 3, 30),
            endDate: new DateOnly(2026, 4, 7));

        Formatter.Format(range, DateContextProvider).Should().Be("March 30 - April 7");
    }

    [Fact]
    public void Format_ReturnsAnchorMonth_WhenVisibleRangeSpansMonthGrid()
    {
        var range = CreateRange(
            CalendarDisplayType.Month,
            anchorDate: new DateOnly(2026, 3, 12),
            startDate: new DateOnly(2026, 2, 23),
            endDate: new DateOnly(2026, 4, 5));

        Formatter.Format(range, DateContextProvider).Should().Be("March 2026");
    }

    [Fact]
    public void Format_ReturnsAnchorMonth_WhenVisibleRangeHasExactlyTwentyEightDays()
    {
        var range = CreateRange(
            CalendarDisplayType.Month,
            anchorDate: new DateOnly(2026, 2, 14),
            startDate: new DateOnly(2026, 2, 1),
            endDate: new DateOnly(2026, 2, 28));

        Formatter.Format(range, DateContextProvider).Should().Be("February 2026");
    }

    private static VisibleDateRange CreateRange(
        CalendarDisplayType displayType,
        DateOnly anchorDate,
        DateOnly startDate,
        DateOnly endDate)
    {
        var dayCount = endDate.DayNumber - startDate.DayNumber + 1;
        var dates = Enumerable.Range(0, dayCount)
                              .Select(offset => startDate.AddDays(offset))
                              .ToArray();

        return new VisibleDateRange(
            displayType,
            anchorDate,
            startDate,
            endDate,
            anchorDate,
            dayCount,
            ContainsToday: false,
            SpansSingleMonth: startDate.Month == endDate.Month && startDate.Year == endDate.Year,
            Dates: dates);
    }

    private sealed class TestDateContextProvider(string cultureName, DateOnly today) : IDateContextProvider
    {
        public System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo(cultureName);
        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
        public DateOnly GetToday() => today;
    }
}
