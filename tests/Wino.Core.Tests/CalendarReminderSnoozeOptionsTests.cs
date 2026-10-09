using FluentAssertions;
using Wino.Core.Domain;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarReminderSnoozeOptionsTests
{
    [Fact]
    public void PreferredLengthIsUsedWhenTheReminderAllowsIt()
        => CalendarReminderSnoozeOptions.GetDefaultSnoozeMinutes(30 * 60, 0, 15).Should().Be(15);

    [Fact]
    public void ShortestAllowedLengthIsUsedWhenThePreferredOneIsTooLong()
        => CalendarReminderSnoozeOptions.GetDefaultSnoozeMinutes(10 * 60, 0, 30).Should().Be(5);

    [Fact]
    public void DefaultReminderDurationCapsTheAllowedLengths()
        => CalendarReminderSnoozeOptions.GetDefaultSnoozeMinutes(60 * 60, 10 * 60, 15).Should().Be(5);

    [Fact]
    public void NoLengthIsOfferedWhenNothingIsAllowed()
    {
        CalendarReminderSnoozeOptions.GetDefaultSnoozeMinutes(0, 0, 5).Should().BeNull();
        CalendarReminderSnoozeOptions.GetDefaultSnoozeMinutes(4 * 60, 0, 5).Should().BeNull();
    }
}
