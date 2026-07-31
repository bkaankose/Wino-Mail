using System.Globalization;
using FluentAssertions;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public class DateTimeDisplayFormatterTests
{
    [Theory]
    [InlineData("cs-CZ", DayHeaderDisplayType.TwentyFourHour)]
    [InlineData("en-US", DayHeaderDisplayType.TwelveHour)]
    public void GetDefaultTimeDisplayType_UsesCultureShortTimePattern(string cultureName, DayHeaderDisplayType expected)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        DateTimeDisplayFormatter.GetDefaultTimeDisplayType(culture).Should().Be(expected);
    }

    [Fact]
    public void FormatTime_UsesTwentyFourHourDisplayForCzechCulture()
    {
        var culture = CultureInfo.GetCultureInfo("cs-CZ");
        var dateTime = new DateTime(2026, 5, 30, 14, 31, 0);

        DateTimeDisplayFormatter.FormatTime(dateTime, DayHeaderDisplayType.TwentyFourHour, culture)
            .Should()
            .Be("14:31");
    }

    [Fact]
    public void CalendarSettings_GetTimeString_UsesConfiguredCultureAndDisplayType()
    {
        var settings = new CalendarSettings(
            DayOfWeek.Monday,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            true,
            DayOfWeek.Monday,
            DayOfWeek.Friday,
            new TimeSpan(9, 0, 0),
            new TimeSpan(18, 0, 0),
            60,
            DayHeaderDisplayType.TwentyFourHour,
            CultureInfo.GetCultureInfo("cs-CZ"));

        settings.GetTimeString(new TimeSpan(14, 31, 0)).Should().Be("14:31");
    }

    [Fact]
    public void CalendarSettings_GetTimeSpan_AcceptsEnglishTwelveHourInputWithTwentyFourHourCulture()
    {
        var settings = new CalendarSettings(
            DayOfWeek.Monday,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            true,
            DayOfWeek.Monday,
            DayOfWeek.Friday,
            new TimeSpan(9, 0, 0),
            new TimeSpan(18, 0, 0),
            60,
            DayHeaderDisplayType.TwentyFourHour,
            CultureInfo.GetCultureInfo("cs-CZ"));

        settings.GetTimeSpan("2:31 PM").Should().Be(new TimeSpan(14, 31, 0));
    }
}
