using FluentAssertions;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class CalendarAgendaTests
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    private static CalendarItemViewModel Event(string title, DateTime start, TimeSpan duration, string? color = null) => new(new CalendarItem
    {
        Id = Guid.NewGuid(),
        Title = title,
        StartDate = start,
        DurationInSeconds = duration.TotalSeconds,
        StartTimeZone = TimeZoneInfo.Local.Id,
        EndTimeZone = TimeZoneInfo.Local.Id,
        CustomEventColorHex = color
    });

    private static DateTime At(DateOnly date, double hours) => date.ToDateTime(TimeOnly.MinValue).AddHours(hours);

    [Fact]
    public void BuildDays_KeepsEmptyDaysUnlessSkipped()
    {
        var standUp = Event("Stand-up", At(Monday, 9), TimeSpan.FromMinutes(30));
        var dates = Enumerable.Range(0, 3).Select(Monday.AddDays).ToList();

        CalendarAgenda.BuildDays(dates, [standUp], skipEmptyDays: false).Select(day => day.Items.Count).Should().Equal(1, 0, 0);
        CalendarAgenda.BuildDays(dates, [standUp], skipEmptyDays: true).Should().ContainSingle().Which.Date.Should().Be(Monday);
    }

    [Fact]
    public void ItemsOn_PutsAllDayFirstThenOrdersByStart()
    {
        var late = Event("Late", At(Monday, 15), TimeSpan.FromHours(1));
        var early = Event("Early", At(Monday, 9), TimeSpan.FromHours(1));
        var allDay = Event("Release", At(Monday, 0), TimeSpan.FromDays(1));

        CalendarAgenda.ItemsOn(Monday, [late, early, allDay]).Select(item => item.Title).Should().Equal("Release", "Early", "Late");
    }

    [Fact]
    public void Texts_FollowTheAgendaRow()
    {
        var item = Event("Review", At(Monday, 11), TimeSpan.FromMinutes(90));
        var allDay = Event("Release", At(Monday, 0), TimeSpan.FromDays(1));

        CalendarAgenda.DurationText(item, Monday).Should().Be("1 h 30 min");
        CalendarAgenda.DurationText(allDay, Monday).Should().BeEmpty();
        CalendarAgenda.StartText(allDay, Monday, null).Should().Be("All day");
        CalendarAgenda.DayTitle(Monday, Monday, System.Globalization.CultureInfo.InvariantCulture).Should().Be("Today · Monday, 12 October");
        CalendarAgenda.DayTitle(Monday.AddDays(1), Monday, System.Globalization.CultureInfo.InvariantCulture).Should().Be("Tuesday, 13 October");
    }

    [Fact]
    public void DayDots_AreDistinctColoursCappedAtThree()
    {
        var items = new[] { "#111111", "#111111", "#222222", "#333333", "#444444" }
            .Select((color, index) => Event($"E{index}", At(Monday, 8 + index), TimeSpan.FromMinutes(30), color))
            .ToList();

        CalendarAgenda.DayDots(Monday, items).Should().Equal("#111111", "#222222", "#333333");
        CalendarAgenda.DayDots(Monday.AddDays(1), items).Should().BeEmpty();
    }

    [Fact]
    public void LoadMessage_UsesTheNavigationDateUnlessDefaultIsRequested()
    {
        var args = new CalendarPageNavigationArgs { NavigationDate = At(Monday, 10), ForceReload = true };
        var message = CalendarPageActivation.CreateLoadMessage(args, CalendarDisplayType.Week, Monday.AddDays(5));
        message.DisplayRequest.Should().Be(new CalendarDisplayRequest(CalendarDisplayType.Week, Monday));
        message.ForceReload.Should().BeTrue();

        var fallback = CalendarPageActivation.CreateLoadMessage(new CalendarPageNavigationArgs { RequestDefaultNavigation = true }, CalendarDisplayType.Month, Monday);
        fallback.DisplayRequest.AnchorDate.Should().Be(Monday);
        CalendarPageActivation.CreateLoadMessage(null, CalendarDisplayType.Day, Monday).ForceReload.Should().BeFalse();
    }
}
