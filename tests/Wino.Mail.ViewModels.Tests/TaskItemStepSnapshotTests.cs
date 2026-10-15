using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Requests;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// A step change followed by a task change (due date, title) must not revert the step: the task snapshot that the
/// page queues is cloned from the item's task entity, steps included.
/// </summary>
public sealed class TaskItemStepSnapshotTests
{
    private static (TaskItemViewModel Item, AccountTaskStep Step) CreateTask()
    {
        var taskId = Guid.NewGuid();
        var step = new AccountTaskStep { Id = Guid.NewGuid(), TaskId = taskId, Title = "Step", IsCompleted = false };
        var task = new AccountTask { Id = taskId, Title = "Task", Steps = [step] };
        return (new TaskItemViewModel(task, "Tasks"), step);
    }

    [Fact]
    public void ApplyStepSnapshot_UpdatesTaskEntitySteps_SoLaterTaskSnapshotKeepsStepState()
    {
        var (item, step) = CreateTask();
        var toggled = RequestEntityCloner.TaskStep(step);
        toggled.IsCompleted = true;

        item.ApplyStepSnapshot(toggled);

        // The page builds the due-date request from a clone of item.Task.
        var dueChange = RequestEntityCloner.Task(item.Task);
        dueChange.DueDate = DateTime.Today.AddDays(1);
        item.ApplySnapshot(dueChange);

        item.Steps.Should().ContainSingle().Which.IsCompleted.Should().BeTrue();
        item.CompletedStepCount.Should().Be(1);
        item.CreateOriginalSnapshot().Steps.Should().ContainSingle().Which.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void ApplyStepSnapshot_AddsNewStep_AndRemoveStepDropsIt()
    {
        var (item, _) = CreateTask();
        var added = new AccountTaskStep { Id = Guid.NewGuid(), TaskId = item.Id, Title = "New" };

        item.ApplyStepSnapshot(added);
        item.Steps.Should().HaveCount(2);
        item.Task.Steps.Should().HaveCount(2);

        item.RemoveStep(added.Id);
        item.Steps.Should().ContainSingle();
        item.Task.Steps.Should().ContainSingle().Which.Id.Should().NotBe(added.Id);
        RequestEntityCloner.Task(item.Task).Steps.Should().ContainSingle();
    }
}
