using FluentAssertions;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Xunit;

namespace Wino.Core.Tests;

/// <summary>New events are never offered read-only calendars.</summary>
public sealed class CalendarReadOnlyFilteringTests
{
    [Fact]
    public void NewEventPicker_LeavesOutReadOnlyCalendars()
    {
        var writable = Calendar("Work", isReadOnly: false);
        var readOnly = Calendar("Holidays", isReadOnly: true);
        var account = Account();

        var groups = CalendarAppShellViewModel.BuildWritableCalendarPickerGroups(
            [Group(account, writable, readOnly)]);

        groups.Should().ContainSingle();
        groups[0].Account.Should().BeSameAs(account);
        groups[0].Calendars.Should().Equal(writable);
    }

    [Fact]
    public void NewEventPicker_DropsAccountsWithoutWritableCalendars()
    {
        var writableAccount = Account();
        var writable = Calendar("Personal", isReadOnly: false);

        var groups = CalendarAppShellViewModel.BuildWritableCalendarPickerGroups(
        [
            Group(Account(), Calendar("Birthdays", isReadOnly: true), Calendar("Subscribed", isReadOnly: true)),
            Group(Account()),
            Group(writableAccount, writable)
        ]);

        groups.Should().ContainSingle();
        groups[0].Account.Should().BeSameAs(writableAccount);
        groups[0].Calendars.Should().Equal(writable);
    }

    [Fact]
    public void NewEventPicker_AllReadOnly_ReturnsNoGroups()
    {
        var groups = CalendarAppShellViewModel.BuildWritableCalendarPickerGroups(
            [Group(Account(), Calendar("Holidays", isReadOnly: true))]);

        groups.Should().BeEmpty();
    }

    [Fact]
    public void EventCompose_OffersOnlyWritableCalendars()
    {
        var writable = Calendar("Work", isReadOnly: false);
        var readOnly = Calendar("Holidays", isReadOnly: true);

        var calendars = CalendarEventComposePageViewModel.SelectWritableCalendars([readOnly, writable]);

        calendars.Should().Equal(writable);
    }

    private static MailAccount Account()
        => new() { Id = Guid.NewGuid(), Address = $"{Guid.NewGuid():N}@example.test", IsCalendarAccessGranted = true };

    private static AccountCalendar Calendar(string name, bool isReadOnly)
        => new() { Id = Guid.NewGuid(), Name = name, IsReadOnly = isReadOnly };

    private static GroupedAccountCalendarViewModel Group(MailAccount account, params AccountCalendar[] calendars)
    {
        foreach (var calendar in calendars) calendar.AccountId = account.Id;
        return new GroupedAccountCalendarViewModel(account, calendars.Select(calendar => new AccountCalendarViewModel(account, calendar)));
    }
}
