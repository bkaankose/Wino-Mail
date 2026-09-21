using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Synchronizers.Mapi;
using Wino.Mapi;
using Wino.Mapi.Rops;
using FluentAssertions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>Tasks over MAPI: row to local task, and the property set written back.</summary>
public class MapiTasksTests
{
    private static readonly MapiTaskTags Tags = new(
        0x81010003, 0x81020005, 0x81040040, 0x81050040, 0x810F0040, 0x811C000B,
        0x8503000B, 0x85020040, 0x85160040, 0x85170040);

    private static readonly AccountTaskList List = new()
    {
        Id = Guid.NewGuid(),
        MailAccountId = Guid.NewGuid(),
        SourceKind = TaskSourceKind.Exchange,
        RemoteId = "mapi:0000000000000010",
        Title = "Tasks"
    };

    [Fact]
    public void Row_MapsToAccountTask_CompletionFromFlagThenStatus()
    {
        var due = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        var done = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        var open = new MapiTaskInfo(1, "IPM.Task", "Ship it", "  ", null, due, null, 2, MapiTaskInfo.StatusInProgress, 0.5, false, null, null);
        var task = MapiTaskMapper.ToAccountTask(open, "mapi:1", List);
        task.TaskListId.Should().Be(List.Id);
        task.MailAccountId.Should().Be(List.MailAccountId);
        task.SourceKind.Should().Be(TaskSourceKind.Exchange);
        task.RemoteId.Should().Be("mapi:1");
        task.Title.Should().Be("Ship it");
        task.Notes.Should().BeNull();
        task.DueDate.Should().Be(due.Date);
        task.IsImportant.Should().BeTrue();
        task.IsCompleted.Should().BeFalse();
        task.CompletedAtUtc.Should().BeNull();
        task.PendingMutation.Should().Be(TaskPendingMutation.None);

        var byStatus = new MapiTaskInfo(2, "IPM.Task", "Done", null, null, null, done, 0, MapiTaskInfo.StatusComplete, 1.0, null, null, null);
        var completed = MapiTaskMapper.ToAccountTask(byStatus, "mapi:2", List);
        completed.IsCompleted.Should().BeTrue();
        completed.CompletedAtUtc.Should().Be(done);
        completed.IsImportant.Should().BeFalse();

        new MapiTaskInfo(3, "IPM.Note", null, null, null, null, null, null, null, null, null, null, null).IsTask.Should().BeFalse();
    }

    [Fact]
    public void Write_KeepsStatusFlagAndPercentInStep()
    {
        var item = new AccountTask { Id = Guid.NewGuid(), Title = "T", Notes = "n", DueDate = new DateTime(2026, 9, 10), IsImportant = true, IsCompleted = true };
        var values = MapiTaskOperations.Properties(Tags, MapiTaskMapper.ToWrite(item), includeClass: true);

        values.Should().Contain(v => v.Tag == PropertyTags.MessageClass && (string)v.Value == "IPM.Task");
        values.Should().Contain(v => v.Tag == PropertyTags.Importance && (uint)v.Value == 2);
        values.Should().Contain(v => v.Tag == Tags.TaskStatus && (uint)v.Value == MapiTaskInfo.StatusComplete);
        values.Should().Contain(v => v.Tag == Tags.TaskComplete && (bool)v.Value);
        values.Should().Contain(v => v.Tag == Tags.PercentComplete && (double)v.Value == 1.0);
        values.Should().Contain(v => v.Tag == Tags.TaskDueDate);
        values.Should().Contain(v => v.Tag == Tags.CommonEnd);
        values.Should().Contain(v => v.Tag == Tags.TaskDateCompleted);

        var open = MapiTaskOperations.Properties(Tags, MapiTaskMapper.ToWrite(new AccountTask { Title = "O" }), includeClass: false);
        open.Should().NotContain(v => v.Tag == PropertyTags.MessageClass);
        open.Should().Contain(v => v.Tag == PropertyTags.Importance && (uint)v.Value == 1);
        open.Should().Contain(v => v.Tag == Tags.TaskStatus && (uint)v.Value == MapiTaskInfo.StatusNotStarted);
        open.Should().NotContain(v => v.Tag == Tags.TaskDueDate);
        open.Should().NotContain(v => v.Tag == Tags.TaskDateCompleted);
    }
}
