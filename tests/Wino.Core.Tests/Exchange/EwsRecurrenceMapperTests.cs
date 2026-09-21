using FluentAssertions;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Synchronizers.Exchange;
using Xunit;

namespace Wino.Core.Tests.Exchange;

public class EwsRecurrenceMapperTests
{
    // Tuesday, the second one of September 2026.
    private static readonly DateTime Start = new(2026, 9, 8, 9, 30, 0);

    [Fact]
    public void Daily_KeepsTheIntervalAndNeverEnds()
    {
        var recurrence = EwsRecurrenceMapper.Create("RRULE:FREQ=DAILY;INTERVAL=3", Start);

        var daily = recurrence.Should().BeOfType<Recurrence.DailyPattern>().Subject;
        daily.Interval.Should().Be(3);
        daily.StartDate.Should().Be(Start.Date);
        daily.HasEnd.Should().BeFalse();
    }

    [Fact]
    public void DailyOnSelectedDays_BecomesAWeeklyPattern()
    {
        // The event composer writes "daily" with the ticked weekdays.
        var recurrence = EwsRecurrenceMapper.Create("RRULE:FREQ=DAILY;INTERVAL=1;BYDAY=MO,WE,FR", Start);

        var weekly = recurrence.Should().BeOfType<Recurrence.WeeklyPattern>().Subject;
        weekly.Interval.Should().Be(1);
        weekly.DaysOfTheWeek.Should().BeEquivalentTo([DayOfTheWeek.Monday, DayOfTheWeek.Wednesday, DayOfTheWeek.Friday]);
    }

    [Fact]
    public void Weekly_WithoutDays_UsesTheStartDay()
    {
        var recurrence = EwsRecurrenceMapper.Create("FREQ=WEEKLY;INTERVAL=2;COUNT=10", Start);

        var weekly = recurrence.Should().BeOfType<Recurrence.WeeklyPattern>().Subject;
        weekly.Interval.Should().Be(2);
        weekly.DaysOfTheWeek.Should().BeEquivalentTo([DayOfTheWeek.Tuesday]);
        weekly.NumberOfOccurrences.Should().Be(10);
    }

    [Theory]
    [InlineData("RRULE:FREQ=MONTHLY;BYMONTHDAY=15", 15)]
    [InlineData("RRULE:FREQ=MONTHLY;BYMONTHDAY=-1", 31)]
    [InlineData("RRULE:FREQ=MONTHLY", 8)]
    public void MonthlyByDate_ReadsTheDayOfTheMonth(string rule, int dayOfMonth)
    {
        var recurrence = EwsRecurrenceMapper.Create(rule, Start);

        recurrence.Should().BeOfType<Recurrence.MonthlyPattern>().Which.DayOfMonth.Should().Be(dayOfMonth);
    }

    [Theory]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=2TU", DayOfTheWeek.Tuesday, DayOfTheWeekIndex.Second)]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=-1FR", DayOfTheWeek.Friday, DayOfTheWeekIndex.Last)]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=TU", DayOfTheWeek.Tuesday, DayOfTheWeekIndex.Second)]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=1", DayOfTheWeek.Weekday, DayOfTheWeekIndex.First)]
    [InlineData("RRULE:FREQ=MONTHLY;BYDAY=SA,SU;BYSETPOS=-1", DayOfTheWeek.WeekendDay, DayOfTheWeekIndex.Last)]
    public void MonthlyByWeekday_ReadsTheDayAndItsOrdinal(string rule, DayOfTheWeek day, DayOfTheWeekIndex index)
    {
        var recurrence = EwsRecurrenceMapper.Create(rule, Start);

        var relative = recurrence.Should().BeOfType<Recurrence.RelativeMonthlyPattern>().Subject;
        relative.DayOfTheWeek.Should().Be(day);
        relative.DayOfTheWeekIndex.Should().Be(index);
    }

    [Fact]
    public void Yearly_UsesTheStartMonthAndDay()
    {
        var recurrence = EwsRecurrenceMapper.Create("RRULE:FREQ=YEARLY;INTERVAL=1", Start);

        var yearly = recurrence.Should().BeOfType<Recurrence.YearlyPattern>().Subject;
        yearly.Month.Should().Be(Month.September);
        yearly.DayOfMonth.Should().Be(8);
    }

    [Fact]
    public void YearlyByWeekday_IsARelativeYearlyPattern()
    {
        var recurrence = EwsRecurrenceMapper.Create("RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=4TH", Start);

        var yearly = recurrence.Should().BeOfType<Recurrence.RelativeYearlyPattern>().Subject;
        yearly.Month.Should().Be(Month.November);
        yearly.DayOfTheWeek.Should().Be(DayOfTheWeek.Thursday);
        yearly.DayOfTheWeekIndex.Should().Be(DayOfTheWeekIndex.Fourth);
    }

    [Fact]
    public void EveryOtherYear_IsExpressedInMonths()
    {
        // An EWS yearly pattern carries no interval.
        var recurrence = EwsRecurrenceMapper.Create("RRULE:FREQ=YEARLY;INTERVAL=2", Start);

        var monthly = recurrence.Should().BeOfType<Recurrence.MonthlyPattern>().Subject;
        monthly.Interval.Should().Be(24);
        monthly.DayOfMonth.Should().Be(8);
    }

    [Theory]
    [InlineData("RRULE:FREQ=WEEKLY;UNTIL=20261231")]
    [InlineData("RRULE:FREQ=WEEKLY;UNTIL=20261231T235959")]
    [InlineData("RRULE:FREQ=WEEKLY;UNTIL=20261231T235959Z")]
    public void Until_EndsTheSeriesOnThatDate(string rule)
    {
        var recurrence = EwsRecurrenceMapper.Create(rule, Start);

        recurrence.EndDate.Should().Be(new DateTime(2026, 12, 31));
        recurrence.NumberOfOccurrences.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RRULE:INTERVAL=2")]
    [InlineData("RRULE:FREQ=HOURLY")]
    public void RulesExchangeCannotHold_MapToNothing(string rule)
        => EwsRecurrenceMapper.Create(rule, Start).Should().BeNull();

    [Fact]
    public void GetRecurrenceRule_PicksTheRuleLineOfTheStoredText()
    {
        var item = new CalendarItem
        {
            Recurrence = string.Join(Constants.CalendarEventRecurrenceRuleSeperator, "EXDATE:20260915T093000", "RRULE:FREQ=WEEKLY;BYDAY=TU")
        };

        EwsRecurrenceMapper.GetRecurrenceRule(item).Should().Be("RRULE:FREQ=WEEKLY;BYDAY=TU");
        EwsRecurrenceMapper.GetRecurrenceRule(new CalendarItem()).Should().BeNull();
    }
}
