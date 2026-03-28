using FluentAssertions;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Extensions;
using Wino.Core.Domain.Entities.Calendar;
using Google.Apis.Calendar.v3.Data;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class CalendarItemTimeZoneDisplayTests
{
    [Fact]
    public void AllDayEvents_KeepTheirOriginalCalendarDates_ForDisplay()
    {
        var calendarItem = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "National Sovereignty and Children's Day",
            StartDate = new DateTime(2026, 4, 23, 0, 0, 0),
            DurationInSeconds = TimeSpan.FromDays(1).TotalSeconds,
            StartTimeZone = "Turkey Standard Time",
            EndTimeZone = "Turkey Standard Time"
        };

        calendarItem.IsAllDayEvent.Should().BeTrue();
        calendarItem.LocalStartDate.Should().Be(new DateTime(2026, 4, 23, 0, 0, 0));
        calendarItem.LocalEndDate.Should().Be(new DateTime(2026, 4, 24, 0, 0, 0));
    }

    [Fact]
    public void EditingAllDayEventDate_DoesNotApplyTimezoneConversion()
    {
        var calendarItem = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Holiday",
            StartDate = new DateTime(2026, 4, 23, 0, 0, 0),
            DurationInSeconds = TimeSpan.FromDays(1).TotalSeconds,
            StartTimeZone = "Turkey Standard Time",
            EndTimeZone = "Turkey Standard Time"
        };

        var viewModel = new CalendarItemViewModel(calendarItem);

        viewModel.StartDate = new DateTime(2026, 4, 24, 0, 0, 0);

        calendarItem.StartDate.Should().Be(new DateTime(2026, 4, 24, 0, 0, 0));
    }

    [Fact]
    public void GmailDateOnlyEvents_KeepFloatingCalendarDates()
    {
        var start = new EventDateTime { Date = "2026-04-23" };
        var end = new EventDateTime { Date = "2026-04-24" };

        GoogleIntegratorExtensions.GetEventLocalDateTime(start).Should().Be(new DateTime(2026, 4, 23, 0, 0, 0));
        GoogleIntegratorExtensions.GetEventLocalDateTime(end).Should().Be(new DateTime(2026, 4, 24, 0, 0, 0));

        GoogleIntegratorExtensions.GetEventDateTimeOffset(start)!.Value.UtcDateTime.Should().Be(new DateTime(2026, 4, 23, 0, 0, 0, DateTimeKind.Utc));
    }
}
