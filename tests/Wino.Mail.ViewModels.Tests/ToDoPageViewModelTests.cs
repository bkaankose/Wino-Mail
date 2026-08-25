using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Tasks;
using Wino.Mail.ViewModels;
using Wino.Messaging.Server;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ToDoPageViewModelTests
{
    [Fact]
    public async Task Reload_EnsuresLocalDefaultListAndEnablesAddTask_WhenProviderTasksAreInactive()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: false);
        var localList = CreateList(account.Id, TaskSourceKind.Local, isDefault: true);
        var taskService = CreateTaskService([localList]);
        taskService.Setup(service => service.GetOrCreateLocalTaskListAsync(account.Id, account.Name)).ReturnsAsync(localList);
        var viewModel = CreateViewModel(taskService.Object, [account]);

        await viewModel.ReloadCommand.ExecuteAsync(null);

        taskService.Verify(service => service.GetOrCreateLocalTaskListAsync(account.Id, account.Name), Times.Once);
        viewModel.TaskLists.Should().ContainSingle().Which.Should().BeSameAs(localList);
        viewModel.CanCreateTask.Should().BeTrue();
        viewModel.AddTaskCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Reload_LeavesAddTaskDisabled_ForReadOnlyProviderList()
    {
        var account = CreateAccount(MailProviderType.Outlook, taskAccess: true);
        var readOnlyList = CreateList(account.Id, TaskSourceKind.Outlook, isDefault: true, isReadOnly: true);
        var taskService = CreateTaskService([readOnlyList]);
        var viewModel = CreateViewModel(taskService.Object, [account]);

        await viewModel.ReloadCommand.ExecuteAsync(null);

        taskService.Verify(service => service.GetOrCreateLocalTaskListAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        viewModel.CanCreateTask.Should().BeFalse();
        viewModel.AddTaskCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task AddTask_PersistsLocallyAndQueuesTaskRequestThroughDelegator()
    {
        var account = CreateAccount(MailProviderType.IMAP4, taskAccess: false);
        var localList = CreateList(account.Id, TaskSourceKind.Local, isDefault: true);
        var taskService = CreateTaskService([localList]);
        taskService.Setup(service => service.GetOrCreateLocalTaskListAsync(account.Id, account.Name)).ReturnsAsync(localList);
        taskService.Setup(service => service.CreateTaskAsync(It.IsAny<AccountTask>()))
            .ReturnsAsync((AccountTask task) => task);
        var delegator = new Mock<IWinoRequestDelegator>();
        delegator.Setup(service => service.ExecuteAsync(It.IsAny<Guid>(), It.IsAny<IEnumerable<IRequestBase>>()))
            .Returns(Task.CompletedTask);
        var viewModel = CreateViewModel(taskService.Object, [account], delegator.Object);
        await viewModel.ReloadCommand.ExecuteAsync(null);
        viewModel.ComposerText = "Follow up";

        await viewModel.AddTaskCommand.ExecuteAsync(null);

        taskService.Verify(service => service.CreateTaskAsync(It.Is<AccountTask>(task =>
            task.MailAccountId == account.Id && task.TaskListId == localList.Id && task.Title == "Follow up")), Times.Once);
        delegator.Verify(service => service.ExecuteAsync(account.Id, It.Is<IEnumerable<IRequestBase>>(requests => IsFollowUpCreateRequest(requests))), Times.Once);
        viewModel.ComposerText.Should().BeEmpty();
    }

    [Fact]
    public async Task Synchronize_PublishesEligibleAccountsWithoutCallingSynchronizationManager()
    {
        var gmail = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        var outlook = CreateAccount(MailProviderType.Outlook, taskAccess: true);
        outlook.IsTaskReauthorizationRequired = true;
        var local = CreateAccount(MailProviderType.IMAP4, taskAccess: false);
        var taskService = CreateTaskService([
            CreateList(gmail.Id, TaskSourceKind.Gmail, isDefault: true),
            CreateList(outlook.Id, TaskSourceKind.Local, isDefault: true),
            CreateList(local.Id, TaskSourceKind.Local, isDefault: true)
        ]);
        taskService.Setup(service => service.GetOrCreateLocalTaskListAsync(outlook.Id, outlook.Name))
            .ReturnsAsync(CreateList(outlook.Id, TaskSourceKind.Local, isDefault: true));
        taskService.Setup(service => service.GetOrCreateLocalTaskListAsync(local.Id, local.Name))
            .ReturnsAsync(CreateList(local.Id, TaskSourceKind.Local, isDefault: true));
        var viewModel = CreateViewModel(taskService.Object, [gmail, outlook, local]);
        await viewModel.ReloadCommand.ExecuteAsync(null);
        var recorder = new TaskSyncRecorder();
        WeakReferenceMessenger.Default.Register<NewTaskSynchronizationRequested>(recorder);

        try
        {
            viewModel.SynchronizeCommand.Execute(null);

            recorder.Messages.Should().ContainSingle().Which.Options.Should().Match<TaskSynchronizationOptions>(options =>
                options.AccountId == gmail.Id && options.Type == TaskSynchronizationType.Delta);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recorder);
        }
    }

    [Fact]
    public async Task ReloadTasks_DoesNotLetOlderSearchResultsReplaceNewerResults()
    {
        var account = CreateAccount(MailProviderType.IMAP4, taskAccess: false);
        var list = CreateList(account.Id, TaskSourceKind.Local, isDefault: true);
        var taskService = CreateTaskService([list]);
        taskService.Setup(service => service.GetOrCreateLocalTaskListAsync(account.Id, account.Name)).ReturnsAsync(list);
        var oldResult = new TaskCompletionSource<List<AccountTask>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newResult = new TaskCompletionSource<List<AccountTask>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        taskService.Setup(service => service.GetTasksAsync(null, null, It.IsAny<TaskViewKind>(), It.IsAny<string>(), It.IsAny<TaskSortKind>()))
            .Returns((Guid? accountId, Guid? listId, TaskViewKind view, string search, TaskSortKind sort) => search switch
            {
                "old" => Observe(oldRequested, oldResult.Task),
                "new" => Observe(newRequested, newResult.Task),
                _ => Task.FromResult(new List<AccountTask>())
            });
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);

        viewModel.SearchText = "old";
        await oldRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SearchText = "new";
        await newRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        newResult.SetResult([CreateTask(account.Id, list.Id, "New result")]);
        await WaitUntilAsync(() => VisibleTaskTitles(viewModel).SequenceEqual(["New result"]));
        oldResult.SetResult([CreateTask(account.Id, list.Id, "Old result")]);
        await Task.Delay(100);

        VisibleTaskTitles(viewModel).Should().Equal("New result");
    }

    private static ToDoPageViewModel CreateViewModel(
        ITaskService taskService,
        IReadOnlyList<MailAccount> accounts,
        IWinoRequestDelegator requestDelegator = null)
    {
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync(accounts.ToList());
        return new ToDoPageViewModel(
            taskService,
            accountService.Object,
            requestDelegator ?? Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<INavigationService>(),
            Mock.Of<ICalendarService>(),
            Mock.Of<IMailDialogService>())
        {
            Dispatcher = new ImmediateDispatcher()
        };
    }

    private static bool IsFollowUpCreateRequest(IEnumerable<IRequestBase> requests)
    {
        var request = requests.SingleOrDefault() as TaskActionRequest;
        return request?.Operation == TaskSynchronizerOperation.CreateTask && request.Task?.Title == "Follow up";
    }

    private static async Task<List<AccountTask>> Observe(TaskCompletionSource requested, Task<List<AccountTask>> result)
    {
        requested.TrySetResult();
        return await result;
    }

    private static AccountTask CreateTask(Guid accountId, Guid listId, string title)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            TaskListId = listId,
            SourceKind = TaskSourceKind.Local,
            Title = title
        };

    private static IEnumerable<string> VisibleTaskTitles(ToDoPageViewModel viewModel)
        => viewModel.TaskGroups.SelectMany(group => group).Select(task => task.Title);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException("The expected To Do state was not observed.");
            await Task.Delay(10);
        }
    }

    private static Mock<ITaskService> CreateTaskService(IReadOnlyList<AccountTaskList> lists)
    {
        var service = new Mock<ITaskService>();
        service.Setup(taskService => taskService.GetTaskListsAsync(null)).ReturnsAsync(lists.ToList());
        service.Setup(taskService => taskService.GetTasksAsync(null, null, It.IsAny<TaskViewKind>(), It.IsAny<string>(), It.IsAny<TaskSortKind>()))
            .ReturnsAsync([]);
        service.Setup(taskService => taskService.GetMyDaySuggestionsAsync()).ReturnsAsync([]);
        return service;
    }

    private static MailAccount CreateAccount(MailProviderType providerType, bool taskAccess)
        => new()
        {
            Id = Guid.NewGuid(),
            Name = providerType.ToString(),
            Address = $"{providerType}@example.test",
            ProviderType = providerType,
            IsTaskAccessGranted = taskAccess
        };

    private static AccountTaskList CreateList(
        Guid accountId,
        TaskSourceKind sourceKind,
        bool isDefault,
        bool isReadOnly = false)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            SourceKind = sourceKind,
            Title = "Tasks",
            IsDefault = isDefault,
            IsReadOnly = isReadOnly
        };

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    internal sealed class TaskSyncRecorder : IRecipient<NewTaskSynchronizationRequested>
    {
        public List<NewTaskSynchronizationRequested> Messages { get; } = [];
        public void Receive(NewTaskSynchronizationRequested message) => Messages.Add(message);
    }
}
