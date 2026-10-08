using System.Threading;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Mail.ViewModels.Companion;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class CompanionDashboardViewModelTests
{
    [Fact]
    public async Task Open_WithNoAccountsReadiness_ShowsNoAccountsWithoutLoading()
    {
        var context = new Context();
        var viewModel = context.Create();

        await viewModel.OpenAsync(CompanionReadinessState.NoAccounts, CancellationToken.None);

        viewModel.SurfaceState.Should().Be(CompanionSurfaceState.NoAccounts);
        viewModel.IsNoAccounts.Should().BeTrue();
        viewModel.IsReady.Should().BeFalse();
        context.Accounts.Verify(service => service.GetAccountsAsync(), Times.Never);
    }

    [Fact]
    public async Task Open_WhileInitializing_ShowsInitializing()
    {
        var context = new Context();
        var viewModel = context.Create();

        await viewModel.OpenAsync(CompanionReadinessState.Initializing, CancellationToken.None);

        viewModel.IsInitializing.Should().BeTrue();
        viewModel.HasContent.Should().BeFalse();
    }

    [Fact]
    public async Task Open_ReadyWithoutStoredAccounts_FallsBackToNoAccounts()
    {
        var context = new Context { StoredAccounts = [] };
        var viewModel = context.Create();

        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);

        viewModel.SurfaceState.Should().Be(CompanionSurfaceState.NoAccounts);
    }

    [Fact]
    public async Task Open_WithEverySectionOff_IsReadyAndSkipsSectionQueries()
    {
        var context = new Context();
        var viewModel = context.Create();

        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);

        viewModel.IsReady.Should().BeTrue();
        viewModel.HasPersonalizedContent.Should().BeFalse();
        viewModel.IsAllCaughtUp.Should().BeFalse("nothing personal is shown, so there is nothing to be caught up on");
        viewModel.GreetingText.Should().NotBeNullOrWhiteSpace();
        context.Tasks.Verify(service => service.GetTasksAsync(It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<TaskViewKind>(), It.IsAny<string>(), It.IsAny<TaskSortKind>()), Times.Never);
        context.Calendar.Verify(service => service.GetAccountCalendarsAsync(It.IsAny<Guid>()), Times.Never);
        context.UnreadBadges.Verify(service => service.GetSnapshotAsync(), Times.Never);
    }

    [Fact]
    public async Task Open_WithTasksShown_LoadsMyDayTasksAndShowsContent()
    {
        var context = new Context();
        context.Preferences.SetupGet(service => service.ShowTasksInCompanion).Returns(true);
        context.StoredTasks.Add(new AccountTask { Title = "Write report", TaskListId = context.TaskListId });
        context.StoredTasks.Add(new AccountTask { Title = "Done already", TaskListId = context.TaskListId, IsCompleted = true });
        var viewModel = context.Create();

        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);

        viewModel.HasTasks.Should().BeTrue();
        viewModel.HasContent.Should().BeTrue();
        viewModel.IsAllCaughtUp.Should().BeFalse();
        viewModel.Tasks.Select(task => task.Title).Should().Equal("Write report", "Done already");
        viewModel.TaskProgressText.Should().Be("1 of 2");
        context.Tasks.Verify(service => service.GetTasksAsync(null, null, TaskViewKind.MyDay, null, TaskSortKind.Importance), Times.Once);
    }

    [Fact]
    public async Task Open_WithSectionsShownButNothingPending_IsAllCaughtUp()
    {
        var context = new Context();
        context.Preferences.SetupGet(service => service.ShowTasksInCompanion).Returns(true);
        context.Preferences.SetupGet(service => service.ShowCalendarInCompanion).Returns(true);
        var viewModel = context.Create();

        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);

        viewModel.IsAllCaughtUp.Should().BeTrue();
        viewModel.HasContent.Should().BeFalse();
        viewModel.HasEvent.Should().BeFalse();
        context.Calendar.Verify(service => service.GetAccountCalendarsAsync(context.AccountId), Times.Once);
    }

    [Fact]
    public async Task Open_WhenLoadingFails_ShowsUnavailableWithTheError()
    {
        var context = new Context();
        context.Preferences.SetupGet(service => service.ShowTasksInCompanion).Returns(true);
        context.Tasks
            .Setup(service => service.GetTasksAsync(It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<TaskViewKind>(), It.IsAny<string>(), It.IsAny<TaskSortKind>()))
            .ThrowsAsync(new InvalidOperationException("database offline"));
        var viewModel = context.Create();

        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);

        viewModel.IsUnavailable.Should().BeTrue();
        viewModel.ErrorText.Should().Be("database offline");
        viewModel.HasError.Should().BeTrue();
    }

    [Fact]
    public async Task SurfaceStateChange_RaisesTheMatchingBooleans()
    {
        var context = new Context();
        var viewModel = context.Create();
        var raised = new List<string>();
        viewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName!);

        await viewModel.OpenAsync(CompanionReadinessState.NoAccounts, CancellationToken.None);

        raised.Should().Contain([nameof(viewModel.SurfaceState), nameof(viewModel.IsNoAccounts), nameof(viewModel.IsInitializing), nameof(viewModel.HasContent)]);
    }

    [Fact]
    public async Task MailMessages_WhileOpen_AreDebouncedIntoOneRefresh()
    {
        var context = new Context();
        var viewModel = context.Create();
        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);
        context.Accounts.Invocations.Clear();

        for (var index = 0; index < 5; index++)
            context.Messenger.Send(new MailAddedMessage(new MailCopy()));

        await WaitUntilAsync(() => context.Accounts.Invocations.Count > 0);
        await Task.Delay(CompanionDashboardViewModel.RefreshDebounce * 3);

        context.Accounts.Verify(service => service.GetAccountsAsync(), Times.Once);
        viewModel.IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task Messages_AfterClose_DoNotRefresh()
    {
        var context = new Context();
        var viewModel = context.Create();
        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);
        viewModel.IsOpen.Should().BeTrue();
        viewModel.Close();
        viewModel.IsOpen.Should().BeFalse();
        context.Accounts.Invocations.Clear();

        context.Messenger.Send(new MailAddedMessage(new MailCopy()));
        await Task.Delay(CompanionDashboardViewModel.RefreshDebounce * 3);

        context.Accounts.Verify(service => service.GetAccountsAsync(), Times.Never);
    }

    [Fact]
    public async Task SectionPreferenceChange_WhileOpen_ReloadsContent()
    {
        var context = new Context();
        var viewModel = context.Create();
        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);
        context.Tasks.Invocations.Clear();

        context.Preferences.SetupGet(service => service.ShowTasksInCompanion).Returns(true);
        context.StoredTasks.Add(new AccountTask { Title = "New task", TaskListId = context.TaskListId });
        context.Preferences.Raise(service => service.PreferenceChanged += null, context.Preferences.Object, nameof(IPreferencesService.ShowTasksInCompanion));

        await WaitUntilAsync(() => viewModel.HasTasks);
        viewModel.Tasks.Should().ContainSingle(task => task.Title == "New task");
    }

    [Fact]
    public async Task SnoozeState_IsReadOnRefresh()
    {
        var context = new Context();
        var until = DateTimeOffset.Now.AddHours(1);
        context.Policy
            .Setup(service => service.GetSnoozeState(It.IsAny<DateTimeOffset>()))
            .Returns(new NotificationSnoozeState(true, NotificationSnoozePreset.OneHour, until));
        var viewModel = context.Create();

        await viewModel.OpenAsync(CompanionReadinessState.Ready, CancellationToken.None);

        viewModel.SnoozeNotifications.Should().BeTrue();
        viewModel.SnoozedUntil.Should().Be(until);
        viewModel.SnoozeInfoText.Should().NotBeNullOrWhiteSpace();
        viewModel.IsCustomSnoozeVisible.Should().BeFalse();
    }

    [Fact]
    public async Task NavigationCommand_Succeeds_RaisesNavigationCompleted()
    {
        var context = new Context();
        var viewModel = context.Create();
        var completed = 0;
        viewModel.NavigationCompleted += (_, _) => completed++;

        await viewModel.OpenCalendarCommand.ExecuteAsync(null);

        completed.Should().Be(1);
        context.Actions.Verify(actions => actions.OpenCalendarAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NavigationCommand_Fails_ReportsTheErrorInsteadOfClosing()
    {
        var context = new Context();
        context.Actions.Setup(actions => actions.OpenSettingsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("no window"));
        var viewModel = context.Create();
        var completed = 0;
        viewModel.NavigationCompleted += (_, _) => completed++;

        await viewModel.OpenSettingsCommand.ExecuteAsync(null);

        completed.Should().Be(0);
        viewModel.ErrorText.Should().Be("no window");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > timeout) throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(20);
        }
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class Context
    {
        public Guid AccountId { get; } = Guid.NewGuid();
        public Guid TaskListId { get; } = Guid.NewGuid();
        public IMessenger Messenger { get; } = new StrongReferenceMessenger();
        public Mock<IAccountService> Accounts { get; } = new();
        public Mock<IPreferencesService> Preferences { get; } = new();
        public Mock<ITaskQueryService> Tasks { get; } = new();
        public Mock<ICalendarService> Calendar { get; } = new();
        public Mock<IUnreadBadgeService> UnreadBadges { get; } = new();
        public Mock<INotificationPolicyService> Policy { get; } = new();
        public Mock<ICompanionActionHandler> Actions { get; } = new();
        public List<MailAccount>? StoredAccounts { get; set; }
        public List<AccountTask> StoredTasks { get; } = [];

        public Context()
        {
            Accounts.Setup(service => service.GetAccountsAsync())
                .ReturnsAsync(() => StoredAccounts ?? [new MailAccount { Id = AccountId, Name = "Work", Address = "me@example.com" }]);
            Tasks
                .Setup(service => service.GetTasksAsync(It.IsAny<Guid?>(), It.IsAny<Guid?>(), It.IsAny<TaskViewKind>(), It.IsAny<string>(), It.IsAny<TaskSortKind>()))
                .ReturnsAsync(() => StoredTasks.ToList());
            Tasks
                .Setup(service => service.GetTaskListsAsync(It.IsAny<Guid?>()))
                .ReturnsAsync(() => [new AccountTaskList { Id = TaskListId, Title = "Tasks" }]);
            Calendar.Setup(service => service.GetAccountCalendarsAsync(It.IsAny<Guid>())).ReturnsAsync([]);
            Policy.Setup(service => service.GetSnoozeState(It.IsAny<DateTimeOffset>())).Returns(NotificationSnoozeState.Delivering);
        }

        public CompanionDashboardViewModel Create()
        {
            var services = new Mock<IServiceProvider>();
            services.Setup(provider => provider.GetService(typeof(IMessenger))).Returns(Messenger);
            services.Setup(provider => provider.GetService(typeof(IAccountService))).Returns(Accounts.Object);
            services.Setup(provider => provider.GetService(typeof(IPreferencesService))).Returns(Preferences.Object);
            services.Setup(provider => provider.GetService(typeof(ITaskQueryService))).Returns(Tasks.Object);
            services.Setup(provider => provider.GetService(typeof(ICalendarService))).Returns(Calendar.Object);
            services.Setup(provider => provider.GetService(typeof(IUnreadBadgeService))).Returns(UnreadBadges.Object);
            services.Setup(provider => provider.GetService(typeof(INotificationPolicyService))).Returns(Policy.Object);

            return new CompanionDashboardViewModel(services.Object, Actions.Object, new InlineDispatcher(), DateTimeOffset.UtcNow);
        }
    }
}
