using System.ComponentModel;
using FluentAssertions;
using Wino.Mail.Controls.Core;
using Xunit;

namespace Wino.Mail.Controls.Tests;

/// <summary>
/// The selection rule the native lists share (Mac, iPhone, iPad, Android): native rows become the snapshot the mail list
/// ViewModel applies.
/// </summary>
public sealed class MailListSelectionSnapshotBuilderTests
{
    [Fact]
    public void ThreadHead_SelectsEveryLeaf_AndTheWholeThread()
    {
        var head = new TestItem("thread-a", DateTimeOffset.Now);
        var leaf = new TestItem("thread-a", DateTimeOffset.Now.AddMinutes(-1));
        var single = new TestItem(null, DateTimeOffset.Now.AddMinutes(-2));
        var collection = new MailListCollection<TestItem>();
        collection.AddRange([head, leaf, single]);
        using var projection = new MailListProjection(collection, new MailListProjectionOptions { GroupMode = MailListGroupMode.None });
        var headRow = projection.Groups.SelectMany(static group => group).Single(static row => row.IsThreadHead);

        var result = MailListSelectionSnapshotBuilder.Build([headRow], projection);

        result.Snapshot.SelectedItems.Should().BeEquivalentTo(new IMailListSourceItem[] { head, leaf });
        result.Snapshot.FullySelectedThreadKeys.Should().Contain("thread-a");
        result.Snapshot.ActiveItem.Should().BeSameAs(headRow.SourceItem);
    }

    [Fact]
    public void ActiveRow_WinsOverTheLastSelectedRow_AndTheSignatureFollowsTheSelection()
    {
        var first = new TestItem(null, DateTimeOffset.Now);
        var second = new TestItem(null, DateTimeOffset.Now.AddMinutes(-1));
        var collection = new MailListCollection<TestItem>();
        collection.AddRange([first, second]);
        using var projection = new MailListProjection(collection, new MailListProjectionOptions { GroupMode = MailListGroupMode.None });
        var rows = projection.Groups.SelectMany(static group => group).ToArray();

        var byLast = MailListSelectionSnapshotBuilder.Build(rows, projection);
        var byActive = MailListSelectionSnapshotBuilder.Build(rows, projection, rows[0]);
        var again = MailListSelectionSnapshotBuilder.Build(rows, projection, rows[0]);

        byLast.Snapshot.ActiveItem.Should().BeSameAs(rows[^1].SourceItem);
        byActive.Snapshot.ActiveItem.Should().BeSameAs(rows[0].SourceItem);
        byActive.Signature.Should().NotBe(byLast.Signature);
        again.Signature.Should().Be(byActive.Signature);
        MailListSelectionSnapshotBuilder.Build([], projection).Snapshot.SelectedItems.Should().BeEmpty();
    }

    [Fact]
    public void RowKey_UsesTheThreadKeyForHeads_AndTheMailIdOtherwise()
    {
        var head = new TestItem("thread-b", DateTimeOffset.Now);
        var leaf = new TestItem("thread-b", DateTimeOffset.Now.AddMinutes(-1));
        var collection = new MailListCollection<TestItem>();
        collection.AddRange([head, leaf]);
        using var projection = new MailListProjection(collection, new MailListProjectionOptions { GroupMode = MailListGroupMode.None });
        projection.ExpandThread("thread-b");
        var rows = projection.Groups.SelectMany(static group => group).ToArray();

        MailListSelectionSnapshotBuilder.RowKey(rows.Single(static row => row.IsThreadHead)).Should().Be("T:thread-b");
        rows.Where(static row => row.IsThreadChild).Select(MailListSelectionSnapshotBuilder.RowKey)
            .Should().OnlyContain(static key => key.StartsWith("I:", StringComparison.Ordinal));
    }

    private sealed class TestItem(string? threadKey, DateTimeOffset date) : IMailListSourceItem
    {
#pragma warning disable CS0067 // The projection subscribes; these items never change.
        public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067

        public Guid StableId { get; } = Guid.NewGuid();
        public string? ThreadKey { get; } = threadKey;
        public DateTimeOffset DateSortKey { get; } = date;
        public string NameSortKey => StableId.ToString("N");
        public bool IsPinned => false;
    }
}
