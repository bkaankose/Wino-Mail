using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using System.Collections.Specialized;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Tasks;
using Wino.Mail.ViewModels;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class ToDoPageViewModelTests
{
    [Fact]
    public async Task Reload_UsesQueryOnlyLocalListAndEnablesAddTask_WhenProviderTasksAreInactive()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: false);
        var localList = CreateList(account.Id, TaskSourceKind.Local, isDefault: true);
        var taskService = CreateTaskService([localList]);
        taskService.Setup(service => service.GetOrCreateLocalTaskListAsync(account.Id, account.Name)).ReturnsAsync(localList);
        var viewModel = CreateViewModel(taskService.Object, [account]);

        await viewModel.ReloadCommand.ExecuteAsync(null);

        taskService.Verify(service => service.GetOrCreateLocalTaskListAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
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
    public async Task AddTask_QueuesSnapshotWithoutInvokingMutationService()
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

        taskService.Verify(service => service.CreateTaskAsync(It.IsAny<AccountTask>()), Times.Never);
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
    public async Task ReloadTasks_DoesNotLetOlderSortResultsReplaceNewerResults()
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
            .Returns((Guid? accountId, Guid? listId, TaskViewKind view, string search, TaskSortKind sort) => sort switch
            {
                TaskSortKind.Importance => Observe(oldRequested, oldResult.Task),
                TaskSortKind.Alphabetical => Observe(newRequested, newResult.Task),
                _ => Task.FromResult(new List<AccountTask>())
            });
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);

        viewModel.SelectedSort = TaskSortKind.Importance;
        await oldRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedSort = TaskSortKind.Alphabetical;
        await newRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        newResult.SetResult([CreateTask(account.Id, list.Id, "New result")]);
        await WaitUntilAsync(() => VisibleTaskTitles(viewModel).SequenceEqual(["New result"]));
        oldResult.SetResult([CreateTask(account.Id, list.Id, "Old result")]);
        await Task.Delay(100);

        VisibleTaskTitles(viewModel).Should().Equal("New result");
    }

    [Fact]
    public async Task Reload_PublishesOnlyStaticSmartViewsBeforeAccountGroups()
    {
        var firstAccount = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        firstAccount.Name = "First";
        firstAccount.Order = 0;
        var secondAccount = CreateAccount(MailProviderType.Outlook, taskAccess: true);
        secondAccount.Name = "Second";
        secondAccount.Order = 1;
        var taskService = CreateTaskService([
            CreateList(firstAccount.Id, TaskSourceKind.Gmail, isDefault: true),
            CreateList(secondAccount.Id, TaskSourceKind.Outlook, isDefault: true)
        ]);
        var viewModel = CreateViewModel(taskService.Object, [firstAccount, secondAccount]);

        await viewModel.ReloadCommand.ExecuteAsync(null);

        viewModel.ShellMenu.Items.Take(3).Should().SatisfyRespectively(
            item => item.Should().BeOfType<MyDayTaskMenuItem>(),
            item => item.Should().BeOfType<PlannedTaskMenuItem>(),
            item => item.Should().BeOfType<ImportantTaskMenuItem>());
        viewModel.ShellMenu.Items.OfType<TaskSmartViewMenuItem>().Should().HaveCount(3);
        viewModel.ShellMenu.Items.OfType<AccountTaskListAccountMenuItem>()
            .Select(item => item.AccountName).Should().Equal("First", "Second");
    }

    [Fact]
    public async Task Reload_ReconcilesShellItemsWithoutResetAndPreservesSelection()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        var list = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: true);
        var lists = new List<AccountTaskList> { list };
        var taskService = CreateTaskService(lists);
        taskService.Setup(service => service.GetTaskListsAsync(null)).ReturnsAsync(() => lists.ToList());
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);
        viewModel.SelectedList = list;
        await WaitUntilAsync(() => viewModel.SelectedList?.Id == list.Id);
        var originalMenuItem = viewModel.ShellMenu.Items.OfType<AccountTaskListMenuItem>().Single();
        var actions = new List<NotifyCollectionChangedAction>();
        viewModel.ShellMenu.Items.CollectionChanged += (_, args) => actions.Add(args.Action);

        var renamed = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: true);
        renamed.Id = list.Id;
        renamed.Title = "Renamed";
        lists = [renamed, CreateList(account.Id, TaskSourceKind.Gmail, isDefault: false)];

        await viewModel.ReloadCommand.ExecuteAsync(null);

        viewModel.ShellMenu.Items.OfType<AccountTaskListMenuItem>().First(item => item.Parameter.Id == list.Id)
            .Should().BeSameAs(originalMenuItem);
        originalMenuItem.Title.Should().Be("Renamed");
        viewModel.SelectedList.Should().BeSameAs(renamed);
        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public async Task Reload_ProjectsEmptyGroupsAndGroupedListsAsNavigationChildren()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        var populated = new AccountTaskListGroup { MailAccountId = account.Id, Title = "Work", SortOrder = 0 };
        var empty = new AccountTaskListGroup { MailAccountId = account.Id, Title = "Empty", SortOrder = 1 };
        var list = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: true);
        list.GroupId = populated.Id;
        var taskService = CreateTaskService([list]);
        taskService.Setup(service => service.GetTaskListGroupsAsync(null)).ReturnsAsync([populated, empty]);
        var viewModel = CreateViewModel(taskService.Object, [account]);

        await viewModel.ReloadCommand.ExecuteAsync(null);

        var groups = viewModel.ShellMenu.Items.OfType<AccountTaskListGroupMenuItem>().ToList();
        groups.Select(group => group.Title).Should().Equal("Work", "Empty");
        groups[0].SubMenuItems.Should().ContainSingle().Which.Should().BeOfType<AccountTaskListMenuItem>();
        groups[1].SubMenuItems.Should().BeEmpty();
    }

    [Fact]
    public async Task DropListOnGroup_MovesOnlyExistingMenuInstancesWithoutReset()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        var first = new AccountTaskListGroup { MailAccountId = account.Id, Title = "First", SortOrder = 0 };
        var second = new AccountTaskListGroup { MailAccountId = account.Id, Title = "Second", SortOrder = 1 };
        var list = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: true);
        list.GroupId = first.Id;
        var taskService = CreateTaskService([list]);
        taskService.Setup(service => service.GetTaskListGroupsAsync(null)).ReturnsAsync([first, second]);
        taskService.Setup(service => service.UpdateTaskListPlacementAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);
        var groups = viewModel.ShellMenu.Items.OfType<AccountTaskListGroupMenuItem>().ToList();
        var listItem = groups[0].SubMenuItems.OfType<AccountTaskListMenuItem>().Single();
        var shellChanges = new List<NotifyCollectionChangedAction>();
        viewModel.ShellMenu.Items.CollectionChanged += (_, args) => shellChanges.Add(args.Action);

        await groups[1].DropRequested(listItem, groups[1], false);

        groups[0].SubMenuItems.Should().BeEmpty();
        groups[1].SubMenuItems.Should().ContainSingle().Which.Should().BeSameAs(listItem);
        list.GroupId.Should().Be(second.Id);
        shellChanges.Should().NotContain(NotifyCollectionChangedAction.Reset);
        taskService.Verify(service => service.UpdateTaskListPlacementAsync(list.Id, second.Id, 0), Times.Once);
    }

    [Fact]
    public async Task DropGroupAfterAnotherGroup_ReordersExistingRootItems()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        var first = new AccountTaskListGroup { MailAccountId = account.Id, Title = "First", SortOrder = 0 };
        var second = new AccountTaskListGroup { MailAccountId = account.Id, Title = "Second", SortOrder = 1 };
        var taskService = CreateTaskService([]);
        taskService.Setup(service => service.GetTaskListGroupsAsync(null)).ReturnsAsync([first, second]);
        taskService.Setup(service => service.UpdateTaskListGroupAsync(It.IsAny<AccountTaskListGroup>())).Returns(Task.CompletedTask);
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);
        var original = viewModel.ShellMenu.Items.OfType<AccountTaskListGroupMenuItem>().ToList();

        await original[1].DropRequested(original[0], original[1], true);

        viewModel.ShellMenu.Items.OfType<AccountTaskListGroupMenuItem>().Should().Equal(original[1], original[0]);
        original[0].Parameter.SortOrder.Should().Be(1);
        original[1].Parameter.SortOrder.Should().Be(0);
    }

    [Fact]
    public async Task SmartView_GroupsOpenTasksByAccountAndExcludesCompletedTasks()
    {
        var firstAccount = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        firstAccount.Name = "First";
        var secondAccount = CreateAccount(MailProviderType.Outlook, taskAccess: true);
        secondAccount.Name = "Second";
        var firstList = CreateList(firstAccount.Id, TaskSourceKind.Gmail, isDefault: true);
        var secondList = CreateList(secondAccount.Id, TaskSourceKind.Outlook, isDefault: true);
        var taskService = CreateTaskService([firstList, secondList]);
        taskService.Setup(service => service.GetTasksAsync(null, null, TaskViewKind.MyDay, It.IsAny<string>(), It.IsAny<TaskSortKind>()))
            .ReturnsAsync([
                CreateTask(firstAccount.Id, firstList.Id, "First open"),
                CreateTask(firstAccount.Id, firstList.Id, "First completed", isCompleted: true),
                CreateTask(secondAccount.Id, secondList.Id, "Second open")
            ]);
        var viewModel = CreateViewModel(taskService.Object, [firstAccount, secondAccount]);

        await viewModel.ReloadCommand.ExecuteAsync(null);

        viewModel.TaskGroups.Select(group => group.Key).Should().Equal("First", "Second");
        viewModel.TaskGroups.Should().OnlyContain(group => group.ShowHeader);
        VisibleTaskTitles(viewModel).Should().BeEquivalentTo("First open", "Second open");
    }

    [Fact]
    public async Task NamedListFilters_ReconcileLocallyWithoutAdditionalQueries()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        var list = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: true);
        var tasks = new List<AccountTask>
        {
            CreateTask(account.Id, list.Id, "Open"),
            CreateTask(account.Id, list.Id, "Open important", isImportant: true),
            CreateTask(account.Id, list.Id, "Completed", isCompleted: true),
            CreateTask(account.Id, list.Id, "Completed important", isCompleted: true, isImportant: true)
        };
        var taskService = CreateTaskService([list]);
        taskService.Setup(service => service.GetTasksAsync(null, list.Id, TaskViewKind.All, It.IsAny<string>(), It.IsAny<TaskSortKind>()))
            .ReturnsAsync(tasks);
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);

        viewModel.SelectedList = list;
        await WaitUntilAsync(() => VisibleTaskTitles(viewModel).Count() == 2);
        var queryCount = taskService.Invocations.Count(invocation => invocation.Method.Name == nameof(ITaskQueryService.GetTasksAsync));

        viewModel.IsImportantTasksFilterSelected = true;
        VisibleTaskTitles(viewModel).Should().Equal("Open important");
        viewModel.SelectedTask = viewModel.TaskGroups.Single().Single();

        viewModel.IsCompletedTasksFilterSelected = true;
        viewModel.IsAllTasksFilterSelected.Should().BeTrue();
        VisibleTaskTitles(viewModel).Should().BeEquivalentTo("Open important", "Completed important");
        viewModel.SelectedTask.Should().NotBeNull();

        viewModel.IsImportantTasksFilterSelected = false;
        VisibleTaskTitles(viewModel).Should().BeEquivalentTo("Open", "Open important", "Completed", "Completed important");

        viewModel.FilterText = "important";
        VisibleTaskTitles(viewModel).Should().BeEquivalentTo("Open important", "Completed important");
        viewModel.FilterText = string.Empty;

        viewModel.IsAllTasksFilterSelected = false;
        viewModel.IsAllTasksFilterSelected.Should().BeTrue();
        taskService.Invocations.Count(invocation => invocation.Method.Name == nameof(ITaskQueryService.GetTasksAsync))
            .Should().Be(queryCount);
    }

    [Fact]
    public async Task NamedListFilters_ArePreservedWhenSelectedListChanges()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        var firstList = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: true);
        var secondList = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: false);
        var taskService = CreateTaskService([firstList, secondList]);
        taskService.Setup(service => service.GetTasksAsync(null, It.IsAny<Guid?>(), TaskViewKind.All, It.IsAny<string>(), It.IsAny<TaskSortKind>()))
            .ReturnsAsync([]);
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);

        viewModel.SelectedList = firstList;
        await WaitUntilAsync(() => viewModel.SelectedList?.Id == firstList.Id);
        viewModel.IsCompletedTasksFilterSelected = true;
        viewModel.IsImportantTasksFilterSelected = true;

        viewModel.SelectedList = secondList;
        await WaitUntilAsync(() => viewModel.SelectedList?.Id == secondList.Id);

        viewModel.IsAllTasksFilterSelected.Should().BeTrue();
        viewModel.IsCompletedTasksFilterSelected.Should().BeTrue();
        viewModel.IsImportantTasksFilterSelected.Should().BeTrue();
    }

    [Fact]
    public async Task GlobalTaskSearch_LoadsOwningListAndOpensSelectedTask()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        account.Name = "Work";
        var list = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: true);
        list.Title = "Launch";
        var task = CreateTask(account.Id, list.Id, "Ship release", isCompleted: true);
        var taskService = CreateTaskService([list]);
        taskService.Setup(service => service.GetTasksAsync(null, null, TaskViewKind.All, "ship", It.IsAny<TaskSortKind>()))
            .ReturnsAsync([task]);
        taskService.Setup(service => service.GetTaskAsync(task.Id)).ReturnsAsync(task);
        taskService.Setup(service => service.GetTaskListAsync(list.Id)).ReturnsAsync(list);
        taskService.Setup(service => service.GetTasksAsync(null, list.Id, TaskViewKind.All, It.IsAny<string>(), It.IsAny<TaskSortKind>()))
            .ReturnsAsync([task]);
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);

        var searchResults = await viewModel.SearchTasksAsync("ship", 6, CancellationToken.None);
        var selected = await viewModel.LoadAndSelectTaskAsync(searchResults.Single().Id);

        selected.Should().NotBeNull();
        viewModel.SelectedList.Should().BeSameAs(list);
        viewModel.SelectedTask.Should().BeSameAs(selected);
        viewModel.IsCompletedTasksFilterSelected.Should().BeTrue();
        viewModel.GetTaskSearchSubtitle(task).Should().Be("Work • Launch");
    }

    [Fact]
    public async Task TaskStateUpsert_ReusesVisibleWrapperWithoutReset()
    {
        var account = CreateAccount(MailProviderType.Gmail, taskAccess: true);
        var list = CreateList(account.Id, TaskSourceKind.Gmail, isDefault: true);
        var task = CreateTask(account.Id, list.Id, "Before");
        var taskService = CreateTaskService([list]);
        taskService.Setup(service => service.GetTasksAsync(null, list.Id, TaskViewKind.All, It.IsAny<string>(), It.IsAny<TaskSortKind>()))
            .ReturnsAsync([task]);
        var viewModel = CreateViewModel(taskService.Object, [account]);
        await viewModel.ReloadCommand.ExecuteAsync(null);
        viewModel.SelectedList = list;
        await WaitUntilAsync(() => VisibleTaskTitles(viewModel).SequenceEqual(["Before"]));
        var wrapper = viewModel.TaskGroups.Single().Single();
        viewModel.SelectedTask = wrapper;
        var actions = new List<NotifyCollectionChangedAction>();
        viewModel.TaskGroups.Single().CollectionChanged += (_, args) => actions.Add(args.Action);
        var updated = CreateTask(account.Id, list.Id, "After", isImportant: true);
        updated.Id = task.Id;

        ((IRecipient<TaskStateChanged>)viewModel).Receive(new TaskStateChanged(
            TaskSynchronizerOperation.UpdateTask,
            null,
            updated,
            null,
            OptimisticEntityChange.Upsert,
            EntityUpdateSource.ClientUpdated));

        viewModel.TaskGroups.Single().Single().Should().BeSameAs(wrapper);
        wrapper.Title.Should().Be("After");
        viewModel.SelectedTask.Should().BeSameAs(wrapper);
        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
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

    private static AccountTask CreateTask(
        Guid accountId,
        Guid listId,
        string title,
        bool isCompleted = false,
        bool isImportant = false)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            TaskListId = listId,
            SourceKind = TaskSourceKind.Local,
            Title = title,
            MyDayDateUtc = DateTime.UtcNow.Date,
            IsCompleted = isCompleted,
            IsImportant = isImportant
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
        service.Setup(taskService => taskService.GetTaskListGroupsAsync(null)).ReturnsAsync([]);
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
            IsTaskAccessGranted = taskAccess,
            IsTaskAccessEnabled = true,
            TaskIntegrationSource = taskAccess
                ? AccountIntegrationSource.Provider
                : AccountIntegrationSource.Local
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
