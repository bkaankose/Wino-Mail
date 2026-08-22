using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class IntelligenceFolderNodeTests
{
    private static readonly SemanticIndexFolderCoverageRule DefaultRule =
        SemanticIndexFolderCoverageRule.Latest(string.Empty, 100);

    [Fact]
    public void Build_NestsChildrenUnderTheirParent()
    {
        var roots = Build(
            Folder("inbox", "Inbox"),
            Folder("receipts", "Receipts", parent: "inbox"),
            Folder("sent", "Sent"));

        roots.Select(node => node.RemoteFolderId).Should().Equal("inbox", "sent");
        roots[0].ChildNodes.Should().ContainSingle().Which.RemoteFolderId.Should().Be("receipts");
        roots[0].ChildNodes[0].Parent.Should().BeSameAs(roots[0]);
    }

    /// <summary>
    /// A user folder filed under an excluded one is still a user folder. Dropping it with its
    /// parent would make it unreachable in the editor.
    /// </summary>
    [Fact]
    public void Build_ReparentsChildrenOfExcludedFoldersOntoTheNearestSelectableAncestor()
    {
        var roots = Build(
            Folder("inbox", "Inbox"),
            Folder("junk", "Junk", parent: "inbox", specialFolderType: SpecialFolderType.Junk),
            Folder("kept", "Kept", parent: "junk"));

        roots.Should().ContainSingle().Which.RemoteFolderId.Should().Be("inbox");
        roots[0].ChildNodes.Should().ContainSingle().Which.RemoteFolderId.Should().Be("kept");
    }

    [Fact]
    public void Build_DropsFoldersWithSynchronizationDisabled()
    {
        var roots = Build(
            Folder("inbox", "Inbox"),
            Folder("off", "Off", isSynchronizationEnabled: false));

        roots.Should().ContainSingle().Which.RemoteFolderId.Should().Be("inbox");
    }

    /// <summary>
    /// Inclusion is not a separate switch — a folder is in exactly when its rule selects
    /// something, so the two can never disagree.
    /// </summary>
    [Fact]
    public void IsIncluded_FollowsTheSelectedCount()
    {
        var node = Build(Folder("inbox", "Inbox"))[0];

        node.IsIncluded.Should().BeFalse();

        node.SelectedMessageCount = 42;
        node.IsIncluded.Should().BeTrue();

        node.SelectedMessageCount = 0;
        node.IsIncluded.Should().BeFalse();
    }

    [Fact]
    public void SelectedMessageCount_RaisesChangeNotificationForTheDerivedState()
    {
        var node = Build(Folder("inbox", "Inbox"))[0];
        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.SelectedMessageCount = 5;

        changed.Should().Contain(nameof(IntelligenceFolderNode.IsIncluded));
        changed.Should().Contain(nameof(IntelligenceFolderNode.RowAutomationName));
    }

    [Fact]
    public void Build_StartsNotYetIncludedFoldersOnARuleThatSelectsNothing()
    {
        var roots = Build(Folder("inbox", "Inbox"), Folder("sent", "Sent"));

        roots.Should().OnlyContain(node => node.Rule.LatestMessageCount == 0);
        roots.Should().OnlyContain(node => node.Rule.Mode == SemanticIndexCoverageMode.LatestCount);
    }

    [Fact]
    public void Build_GivesAlreadyIncludedFoldersTheirStoredRuleAndTheRestTheDefault()
    {
        var stored = SemanticIndexFolderCoverageRule.Latest("inbox", 4200);
        var roots = IntelligenceFolderNode.Build(
            [Folder("inbox", "Inbox"), Folder("sent", "Sent"), Folder("archive", "Archive")],
            IntelligenceCoverageInventory.Empty(Guid.NewGuid()),
            new HashSet<string>(["inbox", "sent"], StringComparer.Ordinal),
            new Dictionary<string, SemanticIndexFolderCoverageRule>(StringComparer.Ordinal) { ["inbox"] = stored },
            DefaultRule);

        roots.Single(node => node.RemoteFolderId == "inbox").Rule.Should().Be(stored);

        // Included but without a stored rule of its own, so it falls back to the account default.
        roots.Single(node => node.RemoteFolderId == "sent").Rule.Should()
            .Be(DefaultRule with { RemoteFolderId = "sent" });

        // Never included, so it must select nothing until the user gives it a range.
        roots.Single(node => node.RemoteFolderId == "archive").Rule.LatestMessageCount.Should().Be(0);
    }

    [Fact]
    public void Build_UsesTheFullPathOnlyWhenTwoFoldersShareAName()
    {
        var roots = Build(
            Folder("inbox", "Inbox"),
            Folder("a", "Archive", parent: "inbox"),
            Folder("b", "Archive"));

        var all = roots.SelectMany(node => node.SelfAndDescendants()).ToArray();
        all.Single(node => node.RemoteFolderId == "a").DisplayName.Should().Be("Inbox / Archive");
        all.Single(node => node.RemoteFolderId == "inbox").DisplayName.Should().Be("Inbox");
    }

    [Fact]
    public void Build_TreatsACycleInTheParentChainAsARoot()
    {
        var roots = Build(
            Folder("a", "A", parent: "b"),
            Folder("b", "B", parent: "a"));

        // Neither can be the other's ancestor without looping, so both surface rather than vanish.
        roots.SelectMany(node => node.SelfAndDescendants()).Should().HaveCount(2);
    }

    [Fact]
    public void HasMessages_ReportsWhetherThereIsAnythingToConfigure()
    {
        var inventory = IntelligenceCoverageInventory.Create(Guid.NewGuid(),
            [new IntelligenceCoverageInventoryRow("m1", DateTimeOffset.UtcNow, "inbox")]);
        var roots = IntelligenceFolderNode.Build(
            [Folder("inbox", "Inbox"), Folder("empty", "Empty")],
            inventory,
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, SemanticIndexFolderCoverageRule>(StringComparer.Ordinal),
            DefaultRule);

        roots.Single(node => node.RemoteFolderId == "inbox").HasMessages.Should().BeTrue();
        roots.Single(node => node.RemoteFolderId == "empty").HasMessages.Should().BeFalse();
    }

    private static IReadOnlyList<IntelligenceFolderNode> Build(params MailItemFolder[] folders)
        => Build([], folders);

    private static IReadOnlyList<IntelligenceFolderNode> Build(string[] included, params MailItemFolder[] folders)
        => IntelligenceFolderNode.Build(
            folders,
            IntelligenceCoverageInventory.Empty(Guid.NewGuid()),
            new HashSet<string>(included, StringComparer.Ordinal),
            new Dictionary<string, SemanticIndexFolderCoverageRule>(StringComparer.Ordinal),
            DefaultRule);

    private static MailItemFolder Folder(
        string remoteFolderId,
        string folderName,
        string? parent = null,
        bool isSynchronizationEnabled = true,
        SpecialFolderType specialFolderType = SpecialFolderType.Other)
        => new()
        {
            Id = Guid.NewGuid(),
            RemoteFolderId = remoteFolderId,
            ParentRemoteFolderId = parent,
            FolderName = folderName,
            IsSynchronizationEnabled = isSynchronizationEnabled,
            SpecialFolderType = specialFolderType,
        };
}
