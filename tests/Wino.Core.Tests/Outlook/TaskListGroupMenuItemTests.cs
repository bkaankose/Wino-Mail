using System.Collections.Generic;
using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Xunit;

namespace Wino.Core.Tests.Outlook;

/// <summary>
/// The group flyout offers exactly one of "ungroup lists" and "delete group", decided by whether
/// the group still holds any lists. Delete must never be reachable while lists are inside it.
/// </summary>
public class TaskListGroupMenuItemTests
{
    [Fact]
    public void EmptyLocalGroup_OffersDeleteButNotUngroup()
    {
        var item = CreateMenuItem(TaskSourceKind.Local);

        item.IsEmpty.Should().BeTrue();
        item.CanDelete.Should().BeTrue();
        item.CanUngroup.Should().BeFalse();
    }

    [Fact]
    public void LocalGroupWithLists_OffersUngroupButNotDelete()
    {
        var item = CreateMenuItem(TaskSourceKind.Local);
        item.SubMenuItems.Add(CreateListMenuItem());

        item.IsEmpty.Should().BeFalse();
        item.CanDelete.Should().BeFalse();
        item.CanUngroup.Should().BeTrue();
    }

    [Fact]
    public void NotifyChildrenChanged_RaisesBothVisibilityFlagsWhenTheGroupEmpties()
    {
        // SubMenuItems is mutated in place by the pane reconciler, so the flyout only re-evaluates
        // if the item explicitly announces the change.
        var item = CreateMenuItem(TaskSourceKind.Local);
        item.SubMenuItems.Add(CreateListMenuItem());

        var raised = new List<string>();
        item.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        item.SubMenuItems.Clear();
        item.NotifyChildrenChanged();

        raised.Should().Contain(nameof(AccountTaskListGroupMenuItem.CanDelete));
        raised.Should().Contain(nameof(AccountTaskListGroupMenuItem.CanUngroup));
        item.CanDelete.Should().BeTrue();
        item.CanUngroup.Should().BeFalse();
    }

    [Fact]
    public void OutlookGroup_IsEditableBecauseSubstrateAcceptsGroupWrites()
    {
        // Groups round-trip to Microsoft To Do through the substrate API, so an Outlook group
        // follows the same empty/non-empty rule as a local one.
        var empty = CreateMenuItem(TaskSourceKind.Outlook);
        empty.IsEditable.Should().BeTrue();
        empty.CanDelete.Should().BeTrue();
        empty.CanUngroup.Should().BeFalse();

        var populated = CreateMenuItem(TaskSourceKind.Outlook);
        populated.SubMenuItems.Add(CreateListMenuItem());
        populated.CanDelete.Should().BeFalse();
        populated.CanUngroup.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GmailGroup_OffersNeither(bool hasLists)
    {
        // Google Tasks has no group concept at all, so there is nowhere to write the change.
        var item = CreateMenuItem(TaskSourceKind.Gmail);
        if (hasLists)
            item.SubMenuItems.Add(CreateListMenuItem());

        item.IsEditable.Should().BeFalse();
        item.CanDelete.Should().BeFalse();
        item.CanUngroup.Should().BeFalse();
    }

    private static AccountTaskListGroupMenuItem CreateMenuItem(TaskSourceKind sourceKind)
        => new(new AccountTaskListGroup { Title = "Group", SourceKind = sourceKind });

    private static IMenuItem CreateListMenuItem()
        => new AccountTaskListMenuItem(new AccountTaskList { Title = "List" }, "Account");
}
