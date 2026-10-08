using System.Collections.ObjectModel;
using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarAppShellLifecycleTests
{
    [Fact]
    public void PrepareForShellShutdown_IsIdempotent_AndNextWindowGetsFreshMenu()
    {
        var accountCalendarState = new Mock<IAccountCalendarStateService>();
        accountCalendarState.SetupGet(service => service.GroupedAccountCalendars)
            .Returns(new ReadOnlyObservableCollection<GroupedAccountCalendarViewModel>([]));

        var viewModel = new CalendarAppShellViewModel(
            Mock.Of<IPreferencesService>(),
            Mock.Of<IStatePersistanceService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<ICalendarService>(),
            accountCalendarState.Object,
            Mock.Of<INavigationService>(),
            new Lazy<CalendarPageViewModel>(() => null!),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IDateContextProvider>());
        var firstDispatcher = new ImmediateDispatcher();

        viewModel.Dispatcher = firstDispatcher;
        var firstMenu = viewModel.ShellMenu;

        viewModel.PrepareForShellShutdown();
        viewModel.PrepareForShellShutdown();

        firstMenu.Should().NotBeNull();
        firstMenu!.Items.Should().BeEmpty();
        viewModel.ShellMenu.Should().BeNull();
        accountCalendarState.Verify(service => service.ClearGroupedAccountCalendars(), Times.Once);

        var secondDispatcher = new ImmediateDispatcher();
        viewModel.Dispatcher = secondDispatcher;

        viewModel.ShellMenu.Should().NotBeNull();
        viewModel.ShellMenu.Should().NotBeSameAs(firstMenu);
        // New event and the date picker. Synchronization moved to the shell title bar, so
        // the pane no longer carries an entry for it.
        viewModel.ShellMenu!.Items.Should().HaveCount(2);
        accountCalendarState.VerifySet(service => service.Dispatcher = secondDispatcher, Times.Once);
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
