using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Tasks;
using Wino.Core.Domain.Misc;
using Wino.Core.Requests;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class TaskServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _database = null!;
    private TaskService _taskService = null!;
    private MailAccount _imapAccount = null!;

    public async Task InitializeAsync()
    {
        _database = new InMemoryDatabaseService();
        await _database.InitializeAsync();
        _taskService = new TaskService(_database);
        _imapAccount = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Local IMAP",
            Address = "local@example.test",
            ProviderType = MailProviderType.IMAP4
        };
        await _database.Connection.InsertAsync(_imapAccount, typeof(MailAccount));
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task GetOrCreateLocalTaskListAsync_CreatesOneDefaultList()
    {
        var first = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var second = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);

        second.Id.Should().Be(first.Id);
        first.SourceKind.Should().Be(TaskSourceKind.Local);
        first.IsDefault.Should().BeTrue();
        (await _taskService.GetTaskListsAsync(_imapAccount.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task NewTaskLists_ReceiveDistinctSharedPaletteColorsAndKeepThemOnRemoteUpdate()
    {
        var local = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var remote = await _taskService.UpsertRemoteTaskListAsync(new AccountTaskList
        {
            MailAccountId = _imapAccount.Id,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = "remote-list",
            Title = "Remote",
            ColorHex = local.ColorHex
        });

        local.ColorHex.Should().BeOneOf(ColorPalette.GetColors());
        remote.ColorHex.Should().BeOneOf(ColorPalette.GetColors());
        remote.ColorHex.Should().NotBe(local.ColorHex);

        var updated = await _taskService.UpsertRemoteTaskListAsync(new AccountTaskList
        {
            MailAccountId = _imapAccount.Id,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = "remote-list",
            Title = "Renamed"
        });

        updated.ColorHex.Should().Be(remote.ColorHex);
    }

    [Fact]
    public async Task TaskListGroups_AreLocalOrderedAndCanRemainEmpty()
    {
        var first = await _taskService.CreateTaskListGroupAsync(_imapAccount.Id, "First");
        var second = await _taskService.CreateTaskListGroupAsync(_imapAccount.Id, "Second");
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);

        await _taskService.UpdateTaskListPlacementAsync(list.Id, second.Id, 0);
        first.SortOrder = 1;
        second.SortOrder = 0;
        await _taskService.UpdateTaskListGroupAsync(first);
        await _taskService.UpdateTaskListGroupAsync(second);

        (await _taskService.GetTaskListGroupsAsync(_imapAccount.Id)).Select(group => group.Title)
            .Should().Equal("Second", "First");
        (await _taskService.GetTaskListAsync(list.Id))!.GroupId.Should().Be(second.Id);

        await _taskService.DeleteTaskListGroupAsync(second.Id);

        (await _taskService.GetTaskListGroupsAsync(_imapAccount.Id)).Should().ContainSingle().Which.Id.Should().Be(first.Id);
        (await _taskService.GetTaskListAsync(list.Id))!.GroupId.Should().BeNull();
    }

    [Fact]
    public async Task GetOrCreateLocalTaskListAsync_ReusesDeterministicWritableDefault()
    {
        var older = new AccountTaskList
        {
            Id = Guid.NewGuid(),
            MailAccountId = _imapAccount.Id,
            SourceKind = TaskSourceKind.Local,
            Title = "Older",
            IsDefault = true,
            IsReadOnly = true,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        var newer = new AccountTaskList
        {
            Id = Guid.NewGuid(),
            MailAccountId = _imapAccount.Id,
            SourceKind = TaskSourceKind.Local,
            Title = "Newer",
            IsDefault = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        await _database.Connection.InsertAllAsync(new[] { older, newer }, runInTransaction: true);

        var selected = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var lists = await _taskService.GetTaskListsAsync(_imapAccount.Id);

        selected.Id.Should().Be(older.Id);
        selected.IsDefault.Should().BeTrue();
        selected.IsReadOnly.Should().BeFalse();
        lists.Should().ContainSingle(list => list.IsDefault && list.Id == older.Id);
    }

    [Fact]
    public async Task GetTasksAsync_FiltersPlannedAndCompletedViews()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Local,
            Title = "Planned",
            DueDate = DateTime.UtcNow.Date.AddDays(1)
        });
        await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Local,
            Title = "Completed",
            IsCompleted = true,
            CompletedAtUtc = DateTime.UtcNow
        });
        await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Local,
            Title = "Unplanned"
        });

        (await _taskService.GetTasksAsync(listId: list.Id, view: TaskViewKind.All)).Should().HaveCount(3);
        (await _taskService.GetTasksAsync(listId: list.Id, view: TaskViewKind.Planned))
            .Select(task => task.Title).Should().Equal("Planned");
        (await _taskService.GetTasksAsync(listId: list.Id, view: TaskViewKind.Completed))
            .Select(task => task.Title).Should().Equal("Completed");
    }

    [Fact]
    public async Task GetTasksAsync_FiltersMyDayAndImportantViews()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var today = DateTime.UtcNow.Date;

        await CreateAsync(list.Id, "In my day", myDay: today);
        await CreateAsync(list.Id, "Was in my day", myDay: today.AddDays(-1));
        await CreateAsync(list.Id, "Starred", important: true);
        await CreateAsync(list.Id, "Due today but not pulled in", due: today);

        (await _taskService.GetTasksAsync(listId: list.Id, view: TaskViewKind.MyDay))
            .Select(task => task.Title).Should().Equal("In my day");
        (await _taskService.GetTasksAsync(listId: list.Id, view: TaskViewKind.Important))
            .Select(task => task.Title).Should().Equal("Starred");
    }

    [Fact]
    public async Task GetTasksAsync_AppliesSortAndAlwaysSinksCompleted()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);

        await CreateAsync(list.Id, "Bravo");
        await CreateAsync(list.Id, "Alpha");
        await CreateAsync(list.Id, "Charlie", completed: true);
        await CreateAsync(list.Id, "Delta", important: true);

        (await _taskService.GetTasksAsync(listId: list.Id, sort: TaskSortKind.Alphabetical))
            .Select(task => task.Title).Should().Equal("Alpha", "Bravo", "Delta", "Charlie");

        var byImportance = await _taskService.GetTasksAsync(listId: list.Id, sort: TaskSortKind.Importance);
        byImportance[0].Title.Should().Be("Delta");
        byImportance[^1].Title.Should().Be("Charlie");
    }

    [Fact]
    public async Task GetMyDaySuggestionsAsync_RanksByUrgencyAndExcludesTodaysMyDay()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var today = DateTime.UtcNow.Date;

        await CreateAsync(list.Id, "Overdue", due: today.AddDays(-3));
        await CreateAsync(list.Id, "Due today", due: today);
        await CreateAsync(list.Id, "Yesterday's my day", myDay: today.AddDays(-1));
        await CreateAsync(list.Id, "Recently added");
        await CreateAsync(list.Id, "Already in my day", myDay: today);
        await CreateAsync(list.Id, "Done", completed: true);

        var suggestions = await _taskService.GetMyDaySuggestionsAsync();

        suggestions.Select(task => task.Title)
            .Should().Equal("Overdue", "Due today", "Yesterday's my day", "Recently added");
    }

    [Fact]
    public async Task GetMyDaySuggestionsAsync_SuggestsAnOverdueTaskOnceUnderItsMostUrgentReason()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var today = DateTime.UtcNow.Date;

        // Qualifies as overdue, as yesterday's leftover, and as recently created.
        await CreateAsync(list.Id, "Triple qualifier", due: today.AddDays(-2), myDay: today.AddDays(-1));

        var suggestions = await _taskService.GetMyDaySuggestionsAsync();

        suggestions.Should().ContainSingle().Which.Title.Should().Be("Triple qualifier");
    }

    [Fact]
    public async Task ApplyTaskHierarchyDeltaAsync_UpdatesInPlaceAndKeepsLocalState()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var today = DateTime.UtcNow.Date;

        var local = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Local,
            RemoteId = "remote-1",
            Title = "Synced task",
            IsImportant = true,
            MyDayDateUtc = today
        });

        await _taskService.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta
        {
            TaskListId = list.Id,
            Tasks =
            [
                new AccountTask
                {
                    MailAccountId = _imapAccount.Id,
                    TaskListId = list.Id,
                    SourceKind = TaskSourceKind.Gmail,
                    RemoteId = "remote-1",
                    Title = "Provider title"
                }
            ],
            TaskDeltaLink = "delta"
        });

        var reconciled = (await _taskService.GetTasksAsync(listId: list.Id)).Single();
        reconciled.Id.Should().Be(local.Id);
        reconciled.Title.Should().Be("Provider title");
        reconciled.MyDayDateUtc.Should().Be(today);
        reconciled.IsImportant.Should().BeTrue();
        (await _taskService.GetTaskListAsync(list.Id)).TaskDeltaLink.Should().Be("delta");
    }

    [Fact]
    public async Task ApplyTaskHierarchyDeltaAsync_PreservesPendingRowsAndTargetsExplicitTombstones()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var pending = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = "pending",
            Title = "Local edit",
            PendingMutation = TaskPendingMutation.Update
        });
        var deleted = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = "deleted",
            Title = "Delete me"
        });
        var untouched = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = "untouched",
            Title = "Keep me"
        });
        deleted.PendingMutation = TaskPendingMutation.None;
        untouched.PendingMutation = TaskPendingMutation.None;
        await _database.Connection.UpdateAsync(deleted, typeof(AccountTask));
        await _database.Connection.UpdateAsync(untouched, typeof(AccountTask));

        await _taskService.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta
        {
            TaskListId = list.Id,
            Tasks =
            [
                new AccountTask
                {
                    MailAccountId = _imapAccount.Id,
                    TaskListId = list.Id,
                    SourceKind = TaskSourceKind.Gmail,
                    RemoteId = "pending",
                    Title = "Stale provider title"
                }
            ],
            DeletedTaskRemoteIds = ["deleted"]
        });

        var stored = await _taskService.GetTasksAsync(listId: list.Id);
        stored.Should().Contain(task => task.Id == pending.Id && task.Title == "Local edit");
        stored.Should().Contain(task => task.Id == untouched.Id);
        stored.Should().NotContain(task => task.Id == deleted.Id);
    }

    [Fact]
    public async Task ApplyTaskHierarchyDeltaAsync_PromotionAndDemotionPreserveTheLocalGuid()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var parent = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = "parent",
            Title = "Parent"
        });
        var child = await _taskService.CreateStepAsync(new AccountTaskStep
        {
            MailAccountId = _imapAccount.Id,
            TaskId = parent.Id,
            SourceKind = TaskSourceKind.Gmail,
            RemoteId = "moving",
            Title = "Child"
        });
        parent.PendingMutation = TaskPendingMutation.None;
        child.PendingMutation = TaskPendingMutation.None;
        await _database.Connection.UpdateAsync(parent, typeof(AccountTask));
        await _database.Connection.UpdateAsync(child, typeof(AccountTaskStep));

        await _taskService.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta
        {
            TaskListId = list.Id,
            Tasks =
            [
                new AccountTask
                {
                    MailAccountId = _imapAccount.Id,
                    TaskListId = list.Id,
                    SourceKind = TaskSourceKind.Gmail,
                    RemoteId = "moving",
                    Title = "Promoted"
                }
            ]
        });

        var promoted = (await _taskService.GetTasksAsync(listId: list.Id)).Single(task => task.RemoteId == "moving");
        promoted.Id.Should().Be(child.Id);

        await _taskService.ApplyTaskHierarchyDeltaAsync(new TaskHierarchyDelta
        {
            TaskListId = list.Id,
            Tasks =
            [
                new AccountTask
                {
                    MailAccountId = _imapAccount.Id,
                    TaskListId = list.Id,
                    SourceKind = TaskSourceKind.Gmail,
                    RemoteId = "parent",
                    Title = "Parent",
                    Steps =
                    [
                        new AccountTaskStep
                        {
                            MailAccountId = _imapAccount.Id,
                            SourceKind = TaskSourceKind.Gmail,
                            RemoteId = "moving",
                            Title = "Demoted"
                        }
                    ]
                }
            ],
            AuthoritativeStepParentRemoteIds = ["parent"]
        });

        var reloadedParent = (await _taskService.GetTasksAsync(listId: list.Id)).Single(task => task.RemoteId == "parent");
        reloadedParent.Steps.Should().ContainSingle().Which.Id.Should().Be(child.Id);
        (await _taskService.GetTasksAsync(listId: list.Id)).Should().NotContain(task => task.RemoteId == "moving");
    }

    [Fact]
    public async Task CompleteTaskMutationAsync_AppliesRemoteTaskAndSteps()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var local = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Local,
            Title = "Local title"
        });

        await _taskService.CompleteTaskMutationAsync(local.Id, new AccountTask
        {
            RemoteId = "remote-task",
            RemoteVersion = "etag-2",
            Title = "Provider title",
            Notes = "Provider notes",
            IsCompleted = true,
            Steps =
            [
                new AccountTaskStep
                {
                    RemoteId = "remote-step",
                    Title = "Provider step",
                    IsCompleted = true,
                    Order = 0
                }
            ]
        }, deleted: false);

        var result = await _taskService.GetTaskAsync(local.Id);
        result!.RemoteId.Should().Be("remote-task");
        result.Title.Should().Be("Provider title");
        result.Notes.Should().Be("Provider notes");
        result.IsCompleted.Should().BeTrue();
        result.Steps.Should().ContainSingle();
        result.Steps[0].Title.Should().Be("Provider step");
        result.Steps[0].IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteTaskMutationAsync_AdoptsRemoteDestinationListWithoutDuplicatingTask()
    {
        var source = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, "Source");
        var destination = await _taskService.UpsertRemoteTaskListAsync(new AccountTaskList
        {
            MailAccountId = _imapAccount.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "destination-list",
            Title = "Destination"
        });
        var local = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = source.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "source-remote-id",
            Title = "Move me"
        });

        await _taskService.CompleteTaskMutationAsync(local.Id, new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = destination.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "destination-remote-id",
            Title = "Move me"
        }, deleted: false);

        var moved = await _taskService.GetTaskAsync(local.Id);
        moved.Should().NotBeNull();
        moved!.TaskListId.Should().Be(destination.Id);
        moved.RemoteId.Should().Be("destination-remote-id");
        (await _taskService.GetTasksAsync(listId: source.Id)).Should().BeEmpty();
        (await _taskService.GetTasksAsync(listId: destination.Id)).Should().ContainSingle()
            .Which.Id.Should().Be(local.Id);
    }

    [Fact]
    public async Task CompleteTaskMutationAsync_CommitsRequestedMyDayDate()
    {
        var list = await _taskService.UpsertRemoteTaskListAsync(new AccountTaskList
        {
            MailAccountId = _imapAccount.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "outlook-list",
            Title = "Tasks"
        });
        var local = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "outlook-task",
            Title = "Task"
        });
        var requestedDate = DateTime.UtcNow.Date;
        var mutationResult = RequestEntityCloner.Task(local);
        mutationResult.MyDayDateUtc = requestedDate;

        await _taskService.CompleteTaskMutationAsync(local.Id, mutationResult, deleted: false);

        var reloaded = await _taskService.GetTaskAsync(local.Id);
        reloaded!.MyDayDateUtc.Should().Be(requestedDate);
    }

    [Fact]
    public async Task CompleteTaskMutationAsync_MergesChecklistInPlaceAndPreservesPendingRows()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var task = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "remote-task",
            Title = "Task"
        });
        task.PendingMutation = TaskPendingMutation.None;
        await _database.Connection.UpdateAsync(task);

        var existing = await _taskService.CreateStepAsync(new AccountTaskStep
        {
            MailAccountId = _imapAccount.Id,
            TaskId = task.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "remote-step",
            Title = "Before"
        });
        existing.PendingMutation = TaskPendingMutation.None;
        await _database.Connection.UpdateAsync(existing);

        var pending = await _taskService.CreateStepAsync(new AccountTaskStep
        {
            MailAccountId = _imapAccount.Id,
            TaskId = task.Id,
            SourceKind = TaskSourceKind.Outlook,
            Title = "Pending"
        });
        var createdAt = existing.CreatedAtUtc;

        await _taskService.CompleteTaskMutationAsync(task.Id, new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "remote-task",
            Title = "Task",
            Steps =
            [
                new AccountTaskStep
                {
                    RemoteId = "remote-step",
                    Title = "After",
                    IsCompleted = true
                }
            ]
        }, deleted: false);

        var result = await _taskService.GetTaskAsync(task.Id);
        result.Steps.Should().HaveCount(2);
        result.Steps.Single(step => step.RemoteId == "remote-step").Id.Should().Be(existing.Id);
        result.Steps.Single(step => step.RemoteId == "remote-step").CreatedAtUtc.Should().Be(createdAt);
        result.Steps.Single(step => step.RemoteId == "remote-step").Title.Should().Be("After");
        result.Steps.Should().Contain(step => step.Id == pending.Id && step.PendingMutation == TaskPendingMutation.Create);
    }

    [Fact]
    public async Task CompleteTaskMutationAsync_CorrelatesCreatedChecklistStepByLocalId()
    {
        var list = await _taskService.GetOrCreateLocalTaskListAsync(_imapAccount.Id, _imapAccount.Name);
        var task = await _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "remote-task",
            Title = "Task"
        });
        task.PendingMutation = TaskPendingMutation.None;
        await _database.Connection.UpdateAsync(task);

        var pending = await _taskService.CreateStepAsync(new AccountTaskStep
        {
            MailAccountId = _imapAccount.Id,
            TaskId = task.Id,
            SourceKind = TaskSourceKind.Outlook,
            Title = "New step"
        });

        await _taskService.CompleteTaskMutationAsync(task.Id, new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = list.Id,
            SourceKind = TaskSourceKind.Outlook,
            RemoteId = "remote-task",
            Title = "Task",
            Steps =
            [
                new AccountTaskStep
                {
                    Id = pending.Id,
                    RemoteId = "created-step",
                    Title = "New step"
                }
            ]
        }, deleted: false);

        var result = await _taskService.GetTaskAsync(task.Id);
        result.Steps.Should().ContainSingle();
        result.Steps[0].Id.Should().Be(pending.Id);
        result.Steps[0].RemoteId.Should().Be("created-step");
        result.Steps[0].PendingMutation.Should().Be(TaskPendingMutation.None);
    }

    private Task<AccountTask> CreateAsync(
        Guid listId,
        string title,
        DateTime? due = null,
        DateTime? myDay = null,
        bool important = false,
        bool completed = false)
        => _taskService.CreateTaskAsync(new AccountTask
        {
            MailAccountId = _imapAccount.Id,
            TaskListId = listId,
            SourceKind = TaskSourceKind.Local,
            Title = title,
            DueDate = due,
            MyDayDateUtc = myDay,
            IsImportant = important,
            IsCompleted = completed,
            CompletedAtUtc = completed ? DateTime.UtcNow : null
        });
}
