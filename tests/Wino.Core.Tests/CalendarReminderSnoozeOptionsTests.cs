using FluentAssertions;
using Wino.Core.Domain;
using Xunit;

namespace Wino.Core.Tests;

public class CalendarReminderSnoozeOptionsTests
{
    [Fact]
    public void GetAllowedSnoozeMinutes_WhenDefaultIs15AndReminderIs15_Excludes30()
    {
        var options = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
            reminderDurationInSeconds: 15 * 60,
            defaultReminderDurationInSeconds: 15 * 60);

        options.Should().Equal(5, 10, 15);
    }

    [Fact]
    public void GetAllowedSnoozeMinutes_WhenReminderIs5AndDefaultIs15_DoesNotPassEventStart()
    {
        var options = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
            reminderDurationInSeconds: 5 * 60,
            defaultReminderDurationInSeconds: 15 * 60);

        options.Should().Equal(5);
    }

    [Fact]
    public void GetAllowedSnoozeMinutes_WhenDefaultReminderIsNone_UsesReminderDurationOnly()
    {
        var options = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
            reminderDurationInSeconds: 30 * 60,
            defaultReminderDurationInSeconds: 0);

        options.Should().Equal(5, 10, 15, 30);
    }

    [Fact]
    public void GetAllowedSnoozeMinutes_WhenReminderIsUnderFiveMinutes_ReturnsNoOptions()
    {
        var options = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
            reminderDurationInSeconds: 60,
            defaultReminderDurationInSeconds: 15 * 60);

        options.Should().BeEmpty();
    }
}
