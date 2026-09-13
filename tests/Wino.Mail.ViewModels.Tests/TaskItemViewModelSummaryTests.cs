using FluentAssertions;
using System.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// The task detail drawer pins the title and a status summary above a scrolling body, so the
/// summary's segments and separators have to resolve without the schedule section being visible.
/// </summary>
public sealed class TaskItemViewModelSummaryTests
{
    [Fact]
    public void DetailSummary_WithoutMetadata_IsHidden()
    {
        var item = CreateItem(inMyDay: false);

        item.HasDetailSummary.Should().BeFalse();
        item.ShowSummaryListName.Should().BeFalse();
        item.ShowMyDaySummarySeparator.Should().BeFalse();
        item.ShowListSummarySeparator.Should().BeFalse();
    }

    [Fact]
    public void DetailSummary_WithDueDateOnly_ShowsNoSeparators()
    {
        var item = CreateItem(inMyDay: false);
        item.DueDate = DateTime.Now.Date;

        item.HasDetailSummary.Should().BeTrue();
        item.ShowMyDaySummarySeparator.Should().BeFalse();
        item.ShowListSummarySeparator.Should().BeFalse();
    }

    [Fact]
    public void DetailSummary_WithDueDateAndMyDay_SeparatesTheTwoSegments()
    {
        var item = CreateItem(inMyDay: true);
        item.DueDate = DateTime.Now.Date;

        item.ShowMyDaySummarySeparator.Should().BeTrue();
        item.ShowListSummarySeparator.Should().BeFalse();
    }

    [Fact]
    public void DetailSummary_WithListName_SeparatesTheListSegment()
    {
        var item = CreateItem(inMyDay: true);
        item.ShowListName = true;

        item.ShowSummaryListName.Should().BeTrue();
        item.ShowMyDaySummarySeparator.Should().BeFalse();
        item.ShowListSummarySeparator.Should().BeTrue();
    }

    [Fact]
    public void DetailSummary_WithEmptyListName_HidesTheListSegment()
    {
        var item = CreateItem(inMyDay: true, listName: string.Empty);
        item.ShowListName = true;

        item.ShowSummaryListName.Should().BeFalse();
        item.ShowListSummarySeparator.Should().BeFalse();
    }

    [Fact]
    public void DueDate_Change_NotifiesTheSummary()
    {
        var item = CreateItem(inMyDay: false);
        var changed = new List<string>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.DueDate = DateTime.Now.Date.AddDays(1);

        changed.Should().Contain(nameof(TaskItemViewModel.HasDetailSummary));
        changed.Should().Contain(nameof(TaskItemViewModel.ShowMyDaySummarySeparator));
    }

    [Fact]
    public void MyDay_Change_NotifiesTheSummary()
    {
        var item = CreateItem(inMyDay: false);
        var changed = new List<string>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.MyDayDateUtc = DateTime.UtcNow.Date;

        changed.Should().Contain(nameof(TaskItemViewModel.HasDetailSummary));
        changed.Should().Contain(nameof(TaskItemViewModel.ShowListSummarySeparator));
    }

    [Fact]
    public void StepCounts_FollowCompletion()
    {
        var task = CreateTask(inMyDay: false);
        task.Steps =
        [
            CreateStep(task.Id, "Draft the outline", isCompleted: true),
            CreateStep(task.Id, "Send for review"),
            CreateStep(task.Id, "File the result")
        ];

        var item = new TaskItemViewModel(task, "Tasks");

        item.StepCount.Should().Be(3);
        item.CompletedStepCount.Should().Be(1);

        item.Steps[1].IsCompleted = true;
        item.RefreshStepSummary();

        item.CompletedStepCount.Should().Be(2);
    }

    [Fact]
    public void StepCounts_Notify_WhenAStepIsRemoved()
    {
        var task = CreateTask(inMyDay: false);
        task.Steps = [CreateStep(task.Id, "Draft the outline", isCompleted: true)];

        var item = new TaskItemViewModel(task, "Tasks");
        var changed = new List<string>();
        ((INotifyPropertyChanged)item).PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        item.Steps.RemoveAt(0);

        item.StepCount.Should().Be(0);
        item.CompletedStepCount.Should().Be(0);
        changed.Should().Contain(nameof(TaskItemViewModel.StepCount));
        changed.Should().Contain(nameof(TaskItemViewModel.CompletedStepCount));
    }

    private static TaskItemViewModel CreateItem(bool inMyDay, string listName = "Tasks")
        => new(CreateTask(inMyDay), listName);

    private static AccountTask CreateTask(bool inMyDay)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = Guid.NewGuid(),
            TaskListId = Guid.NewGuid(),
            SourceKind = TaskSourceKind.Local,
            Title = "Ucuncu",
            MyDayDateUtc = inMyDay ? DateTime.UtcNow.Date : null
        };

    private static AccountTaskStep CreateStep(Guid taskId, string title, bool isCompleted = false)
        => new()
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            MailAccountId = Guid.NewGuid(),
            SourceKind = TaskSourceKind.Local,
            Title = title,
            IsCompleted = isCompleted
        };
}
