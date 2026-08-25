using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarImportSelectionTests
{
    [Fact]
    public async Task UniqueAliasMatch_SelectsPrimaryWritableCalendarAndRemovesOwnAttendee()
    {
        var account = CreateAccount("owner@example.com");
        var calendar = CreateCalendar(account, "Primary", isPrimary: true);
        var state = CreateState([calendar]);
        var accounts = CreateAccountService(
            [account],
            new Dictionary<Guid, List<MailAccountAlias>>
            {
                [account.Id] = [CreateAlias(account, "alias@example.com")]
            });
        var dialogs = new Mock<IMailDialogService>();
        var viewModel = CreateViewModel(accounts.Object, state.Object, dialogs.Object);
        var args = new CalendarEventComposeNavigationArgs
        {
            RequireCalendarPickerWhenUnresolved = true,
            AccountAddressHints = ["alias@example.com"],
            Attendees =
            [
                new("Owner", "alias@example.com"),
                new("Guest", "guest@example.com")
            ]
        };

        var handled = await viewModel.PrepareImportedComposeArgsAsync(args);

        handled.Should().BeTrue();
        args.SelectedCalendarId.Should().Be(calendar.Id);
        args.Attendees.Should().ContainSingle().Which.Email.Should().Be("guest@example.com");
        dialogs.Verify(service => service.ShowSingleCalendarPickerDialogAsync(It.IsAny<List<CalendarPickerAccountGroup>>()), Times.Never);
    }

    [Fact]
    public async Task AmbiguousAccountMatch_UsesPickerWithWritableCalendarsOnly()
    {
        var firstAccount = CreateAccount("shared@example.com");
        var secondAccount = CreateAccount("second@example.com");
        var firstWritable = CreateCalendar(firstAccount, "First", isPrimary: true);
        var firstReadOnly = CreateCalendar(firstAccount, "Read only", isPrimary: false, isReadOnly: true);
        var secondWritable = CreateCalendar(secondAccount, "Second", isPrimary: true);
        var state = CreateState([firstWritable, firstReadOnly, secondWritable]);
        var accounts = CreateAccountService(
            [firstAccount, secondAccount],
            new Dictionary<Guid, List<MailAccountAlias>>
            {
                [firstAccount.Id] = [],
                [secondAccount.Id] = [CreateAlias(secondAccount, "shared@example.com")]
            });
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(service => service.ShowSingleCalendarPickerDialogAsync(It.IsAny<List<CalendarPickerAccountGroup>>()))
            .ReturnsAsync(new AccountCalendarPickingResult(secondWritable.AccountCalendar, false));
        var viewModel = CreateViewModel(accounts.Object, state.Object, dialogs.Object);
        var args = new CalendarEventComposeNavigationArgs
        {
            RequireCalendarPickerWhenUnresolved = true,
            AccountAddressHints = ["shared@example.com"],
            Attendees =
            [
                new("Second owner", "second@example.com"),
                new("Guest", "guest@example.com")
            ]
        };

        var handled = await viewModel.PrepareImportedComposeArgsAsync(args);

        handled.Should().BeTrue();
        args.SelectedCalendarId.Should().Be(secondWritable.Id);
        args.Attendees.Select(attendee => attendee.Email).Should().Equal("guest@example.com");
        dialogs.Verify(service => service.ShowSingleCalendarPickerDialogAsync(
            It.Is<List<CalendarPickerAccountGroup>>(groups =>
                groups.SelectMany(group => group.Calendars).All(calendar => !calendar.IsReadOnly) &&
                groups.SelectMany(group => group.Calendars).Count() == 2)), Times.Once);
    }

    [Fact]
    public async Task MissingAccountMatch_UsesPickerAndCancellationReturnsToCalendarRoot()
    {
        var account = CreateAccount("owner@example.com");
        var calendar = CreateCalendar(account, "Primary", isPrimary: true);
        var state = CreateState([calendar]);
        var accounts = CreateAccountService(
            [account],
            new Dictionary<Guid, List<MailAccountAlias>> { [account.Id] = [] });
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(service => service.ShowSingleCalendarPickerDialogAsync(It.IsAny<List<CalendarPickerAccountGroup>>()))
            .ReturnsAsync(new AccountCalendarPickingResult(null, false));
        var viewModel = CreateViewModel(accounts.Object, state.Object, dialogs.Object);
        var args = new CalendarEventComposeNavigationArgs
        {
            RequireCalendarPickerWhenUnresolved = true,
            AccountAddressHints = ["unknown@example.com"]
        };

        var handled = await viewModel.PrepareImportedComposeArgsAsync(args);

        handled.Should().BeFalse();
        args.SelectedCalendarId.Should().BeNull();
        dialogs.Verify(service => service.ShowSingleCalendarPickerDialogAsync(It.IsAny<List<CalendarPickerAccountGroup>>()), Times.Once);
    }

    [Fact]
    public async Task ReadOnlyCalendarsOnly_ReturnsFalseAndShowsWarning()
    {
        var account = CreateAccount("owner@example.com");
        var readOnly = CreateCalendar(account, "Read only", isPrimary: true, isReadOnly: true);
        var state = CreateState([readOnly]);
        var accounts = CreateAccountService(
            [account],
            new Dictionary<Guid, List<MailAccountAlias>> { [account.Id] = [] });
        var dialogs = new Mock<IMailDialogService>();
        var viewModel = CreateViewModel(accounts.Object, state.Object, dialogs.Object);
        var args = new CalendarEventComposeNavigationArgs
        {
            RequireCalendarPickerWhenUnresolved = true,
            AccountAddressHints = [account.Address]
        };

        var handled = await viewModel.PrepareImportedComposeArgsAsync(args);

        handled.Should().BeFalse();
        dialogs.Verify(service => service.InfoBarMessage(
            It.IsAny<string>(),
            It.IsAny<string>(),
            Wino.Core.Domain.Enums.InfoBarMessageType.Warning), Times.Once);
    }

    private static CalendarAppShellViewModel CreateViewModel(
        IAccountService accountService,
        IAccountCalendarStateService state,
        IMailDialogService dialogs)
        => new(
            Mock.Of<IPreferencesService>(),
            Mock.Of<IStatePersistanceService>(),
            accountService,
            Mock.Of<ICalendarService>(),
            state,
            Mock.Of<INavigationService>(),
            new Lazy<CalendarPageViewModel>(() => null!),
            dialogs,
            Mock.Of<IDateContextProvider>());

    private static Mock<IAccountCalendarStateService> CreateState(IReadOnlyList<AccountCalendarViewModel> calendars)
    {
        var grouped = calendars
            .GroupBy(calendar => calendar.Account.Id)
            .Select(group => new GroupedAccountCalendarViewModel(group.First().Account, group))
            .ToList();
        var observableGroups = new ObservableCollection<GroupedAccountCalendarViewModel>(grouped);
        var state = new Mock<IAccountCalendarStateService>();
        state.SetupGet(service => service.AllCalendars).Returns(calendars);
        state.SetupGet(service => service.GroupedAccountCalendars)
            .Returns(new ReadOnlyObservableCollection<GroupedAccountCalendarViewModel>(observableGroups));
        return state;
    }

    private static Mock<IAccountService> CreateAccountService(
        List<MailAccount> accounts,
        IReadOnlyDictionary<Guid, List<MailAccountAlias>> aliases)
    {
        var service = new Mock<IAccountService>();
        service.Setup(value => value.GetAccountsAsync()).ReturnsAsync(accounts);

        foreach (var pair in aliases)
            service.Setup(value => value.GetAccountAliasesAsync(pair.Key)).ReturnsAsync(pair.Value);

        return service;
    }

    private static MailAccount CreateAccount(string address)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = address,
            Address = address,
            IsCalendarAccessGranted = true
        };

    private static MailAccountAlias CreateAlias(MailAccount account, string address)
        => new()
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            AliasAddress = address
        };

    private static AccountCalendarViewModel CreateCalendar(
        MailAccount account,
        string name,
        bool isPrimary,
        bool isReadOnly = false)
        => new(
            account,
            new AccountCalendar
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                Name = name,
                IsPrimary = isPrimary,
                IsReadOnly = isReadOnly
            });
}
