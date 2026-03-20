using System.Globalization;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public class CalendarRangeResolverTests
{
    [Fact]
    public void Resolve_Day_ReturnsAnchorDateOnly()
    {
        var settings = CreateSettings();
        var today = new DateOnly(2026, 3, 20);

        var range = CalendarRangeResolver.Resolve(new CalendarDisplayRequest(CalendarDisplayType.Day, today), settings, today);

        range.StartDate.Should().Be(today);
        range.EndDate.Should().Be(today);
        range.DayCount.Should().Be(1);
        range.ContainsToday.Should().BeTrue();
        range.Dates.Should().ContainSingle().Which.Should().Be(today);
    }

    [Fact]
    public void Resolve_Week_HonorsConfiguredFirstDayOfWeek()
    {
        var settings = CreateSettings(firstDayOfWeek: DayOfWeek.Sunday);
        var anchor = new DateOnly(2026, 3, 18);

        var range = CalendarRangeResolver.Resolve(new CalendarDisplayRequest(CalendarDisplayType.Week, anchor), settings, today: anchor);

        range.StartDate.Should().Be(new DateOnly(2026, 3, 15));
        range.EndDate.Should().Be(new DateOnly(2026, 3, 21));
        range.DayCount.Should().Be(7);
    }

    [Fact]
    public void Resolve_WorkWeek_UsesConfiguredBounds()
    {
        var settings = CreateSettings(
            firstDayOfWeek: DayOfWeek.Sunday,
            workWeekStart: DayOfWeek.Monday,
            workWeekEnd: DayOfWeek.Thursday);
        var anchor = new DateOnly(2026, 3, 18);

        var range = CalendarRangeResolver.Resolve(new CalendarDisplayRequest(CalendarDisplayType.WorkWeek, anchor), settings, today: anchor);

        range.StartDate.Should().Be(new DateOnly(2026, 3, 16));
        range.EndDate.Should().Be(new DateOnly(2026, 3, 19));
        range.DayCount.Should().Be(4);
    }

    [Fact]
    public void Resolve_Month_CoversEntireAnchorMonth()
    {
        var settings = CreateSettings();
        var anchor = new DateOnly(2026, 2, 14);

        var range = CalendarRangeResolver.Resolve(new CalendarDisplayRequest(CalendarDisplayType.Month, anchor), settings, today: anchor);

        range.StartDate.Should().Be(new DateOnly(2026, 2, 1));
        range.EndDate.Should().Be(new DateOnly(2026, 2, 28));
        range.DayCount.Should().Be(28);
        range.SpansSingleMonth.Should().BeTrue();
    }

    [Theory]
    [InlineData(CalendarDisplayType.Day, 2026, 3, 18, 2026, 3, 19, 2026, 3, 17)]
    [InlineData(CalendarDisplayType.Week, 2026, 3, 18, 2026, 3, 25, 2026, 3, 11)]
    [InlineData(CalendarDisplayType.WorkWeek, 2026, 3, 18, 2026, 3, 25, 2026, 3, 11)]
    [InlineData(CalendarDisplayType.Month, 2026, 3, 18, 2026, 4, 18, 2026, 2, 18)]
    public void Navigate_MovesExactlyOnePeriod(
        CalendarDisplayType displayType,
        int year,
        int month,
        int day,
        int nextYear,
        int nextMonth,
        int nextDay,
        int previousYear,
        int previousMonth,
        int previousDay)
    {
        var settings = CreateSettings();
        var today = new DateOnly(2026, 3, 20);
        var current = CalendarRangeResolver.Resolve(
            new CalendarDisplayRequest(displayType, new DateOnly(year, month, day)),
            settings,
            today);

        var next = CalendarRangeResolver.Navigate(current, 1, settings, today);
        var previous = CalendarRangeResolver.Navigate(current, -1, settings, today);

        next.AnchorDate.Should().Be(new DateOnly(nextYear, nextMonth, nextDay));
        previous.AnchorDate.Should().Be(new DateOnly(previousYear, previousMonth, previousDay));
    }

    [Fact]
    public void ChangeDisplayType_FromMonth_UsesTodayWhenTodayIsInsideCurrentMonth()
    {
        var settings = CreateSettings();
        var today = new DateOnly(2026, 3, 20);
        var monthRange = CalendarRangeResolver.Resolve(
            new CalendarDisplayRequest(CalendarDisplayType.Month, new DateOnly(2026, 3, 5)),
            settings,
            today);

        var dayRange = CalendarRangeResolver.ChangeDisplayType(monthRange, CalendarDisplayType.Day, settings, today);

        dayRange.AnchorDate.Should().Be(today);
        dayRange.StartDate.Should().Be(today);
        dayRange.EndDate.Should().Be(today);
    }

    [Fact]
    public void Formatter_Day_UsesSingleDate()
    {
        var formatter = new CalendarRangeTextFormatter();
        var range = new VisibleDateRange(
            CalendarDisplayType.Day,
            new DateOnly(2026, 3, 20),
            new DateOnly(2026, 3, 20),
            new DateOnly(2026, 3, 20),
            new DateOnly(2026, 3, 20),
            1,
            true,
            true,
            [new DateOnly(2026, 3, 20)]);

        var text = formatter.Format(range, new TestDateContextProvider("en-US", today: new DateOnly(2026, 3, 20)));

        text.Should().Be("3/20/2026");
    }

    [Fact]
    public void Formatter_Range_UsesCultureShortDatePattern()
    {
        var formatter = new CalendarRangeTextFormatter();
        var range = new VisibleDateRange(
            CalendarDisplayType.Week,
            new DateOnly(2026, 3, 20),
            new DateOnly(2026, 3, 16),
            new DateOnly(2026, 3, 22),
            new DateOnly(2026, 3, 20),
            7,
            true,
            true,
            [
                new DateOnly(2026, 3, 16),
                new DateOnly(2026, 3, 17),
                new DateOnly(2026, 3, 18),
                new DateOnly(2026, 3, 19),
                new DateOnly(2026, 3, 20),
                new DateOnly(2026, 3, 21),
                new DateOnly(2026, 3, 22)
            ]);

        var text = formatter.Format(range, new TestDateContextProvider("de-DE", today: new DateOnly(2026, 3, 20)));

        text.Should().Be("16.03.2026 - 22.03.2026");
    }

    private static CalendarSettings CreateSettings(
        DayOfWeek firstDayOfWeek = DayOfWeek.Monday,
        DayOfWeek workWeekStart = DayOfWeek.Monday,
        DayOfWeek workWeekEnd = DayOfWeek.Friday,
        string cultureName = "en-US")
    {
        return new CalendarSettings(
            firstDayOfWeek,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            workWeekStart,
            workWeekEnd,
            TimeSpan.FromHours(8),
            TimeSpan.FromHours(17),
            64,
            DayHeaderDisplayType.TwentyFourHour,
            CultureInfo.GetCultureInfo(cultureName));
    }

    private sealed class TestDateContextProvider(string cultureName, DateOnly today) : IDateContextProvider
    {
        public CultureInfo Culture => CultureInfo.GetCultureInfo(cultureName);
        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
        public DateOnly GetToday() => today;
    }
}
