using System.Globalization;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarAccessibilityFormatterTests
{
    private static CalendarSettings Settings(string culture = "en-US", DayHeaderDisplayType clock = DayHeaderDisplayType.TwentyFourHour)
        => new(DayOfWeek.Monday, [], false, DayOfWeek.Monday, DayOfWeek.Friday,
            TimeSpan.FromHours(9), TimeSpan.FromHours(17), 60, clock, CultureInfo.GetCultureInfo(culture));

    [Fact]
    public void SingleAllDayEventAnnouncesOnlyTheOccupiedDate()
    {
        var result = CalendarAccessibilityFormatter.FormatPeriod(new(2026, 10, 7), new(2026, 10, 8), true, Settings());

        result.Should().Contain("Wednesday, October 7, 2026").And.NotContain("October 8").And.NotContain("00:00");
    }

    [Fact]
    public void MultiDayAllDayEventExcludesItsEndBoundary()
    {
        var result = CalendarAccessibilityFormatter.FormatPeriod(new(2026, 12, 30), new(2027, 1, 2), true, Settings());

        result.Should().Contain("December 30, 2026").And.Contain("January 1, 2027").And.NotContain("January 2");
    }

    [Fact]
    public void OvernightTimedEventIncludesBothDatesAndTheConfiguredClock()
    {
        var result = CalendarAccessibilityFormatter.FormatPeriod(new(2026, 10, 7, 23, 30, 0), new(2026, 10, 8, 1, 0, 0), false, Settings());

        result.Should().Contain("October 7, 2026").And.Contain("October 8, 2026").And.Contain("23:30").And.Contain("01:00");
    }

    [Fact]
    public void DateAndClockUseTheCalendarCulture()
    {
        var result = CalendarAccessibilityFormatter.FormatPeriod(new(2026, 10, 7, 13, 30, 0), new(2026, 10, 7, 14, 0, 0), false,
            Settings("en-GB", DayHeaderDisplayType.TwelveHour));

        result.Should().Contain("7 October 2026").And.Contain("1:30").And.NotContain("13:30");
    }

    [Fact]
    public void MissingAllDayDurationDoesNotAnnounceThePreviousDate()
    {
        var result = CalendarAccessibilityFormatter.FormatPeriod(new(2026, 10, 7), new(2026, 10, 7), true, Settings());

        result.Should().Contain("October 7, 2026").And.NotContain("October 6");
    }
}
