using System.Reflection;
using FluentAssertions;
using Microsoft.Graph.Models;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Synchronizers.Mail;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class OutlookTaskPayloadTests
{
    [Fact]
    public void BuildRemoteTask_ForUpdate_OmitsChecklistItems()
    {
        // Graph answers a task PATCH carrying checklistItems with "Update on checklistItems
        // navigation property is not supported in PATCH request on task entity".
        var payload = BuildRemoteTask(CreateTaskWithStep(), includeChecklistItems: false);

        payload.ChecklistItems.Should().BeNull();
        payload.Status.Should().Be(Microsoft.Graph.Models.TaskStatus.Completed);
        payload.Title.Should().Be("task");
    }

    [Fact]
    public void BuildRemoteTask_ForCreate_KeepsChecklistItems()
    {
        // A task POST does accept and persist the checklist inline.
        var task = CreateTaskWithStep();
        var payload = BuildRemoteTask(task, includeChecklistItems: true);

        payload.ChecklistItems.Should().ContainSingle();
        payload.ChecklistItems![0].DisplayName.Should().Be("step");
        payload.ChecklistItems[0].IsChecked.Should().BeTrue();
        var extension = payload.Extensions.Should().ContainSingle().Which.Should().BeOfType<OpenTypeExtension>().Subject;
        extension.ExtensionName.Should().Be("com.winomail.taskIdentity");
        extension.AdditionalData["localTaskId"].Should().Be(task.Id.ToString("D"));
    }

    private static AccountTask CreateTaskWithStep() => new()
    {
        Title = "task",
        IsCompleted = true,
        SourceKind = TaskSourceKind.Outlook,
        Steps =
        [
            new AccountTaskStep { Title = "step", IsCompleted = true, Order = 0, RemoteId = "step-id" }
        ]
    };

    private static TodoTask BuildRemoteTask(AccountTask task, bool includeChecklistItems)
    {
        var method = typeof(OutlookSynchronizer).GetMethod(
            "BuildRemoteTask",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull("OutlookSynchronizer.BuildRemoteTask must exist for this guard to be meaningful");

        return (TodoTask)method!.Invoke(null, [task, includeChecklistItems, includeChecklistItems])!;
    }
}
