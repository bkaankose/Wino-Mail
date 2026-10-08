using System.ComponentModel;
using System.Collections.Specialized;
using FluentAssertions;
using Wino.Mail.Controls.Core;
using Xunit;

namespace Wino.Mail.Controls.Tests;

/// <summary>
/// Covers how the projection publishes rows. A folder switch replaces the whole identity set,
/// which must reach the list as one swap rather than one collection change per row.
/// </summary>
public sealed class MailListProjectionTests
{
    [Fact]
    public void ReplaceAll_PublishesGroupsWholesale_InsteadOfPerRowChanges()
    {
        var collection = new MailListCollection<TestItem>();
        collection.AddRange(CreateItems("old", 5));
        using var projection = new MailListProjection(
            collection,
            new MailListProjectionOptions { GroupMode = MailListGroupMode.None });

        var groupChanges = new List<NotifyCollectionChangedAction>();
        var rowChanges = 0;
        projection.Groups.CollectionChanged += (_, args) => groupChanges.Add(args.Action);
        foreach (var group in projection.Groups)
        {
            group.CollectionChanged += (_, _) => rowChanges++;
        }

        using (collection.DeferRefresh())
        {
            collection.ReplaceAll(CreateItems("new", 40));
        }

        projection.RowCount.Should().Be(40);
        rowChanges.Should().Be(0, "the previous group is discarded rather than mutated row by row");
        groupChanges.Should().OnlyContain(action =>
            action == NotifyCollectionChangedAction.Reset ||
            action == NotifyCollectionChangedAction.Add);
        groupChanges.Count.Should().BeLessThan(5);
    }

    [Fact]
    public void ReplaceAll_RaisesGroupResetEvents_ForHostsThatDetachTheirItemsSource()
    {
        var collection = new MailListCollection<TestItem>();
        collection.AddRange(CreateItems("old", 3));
        using var projection = new MailListProjection(collection);

        var resetting = 0;
        var reset = 0;
        projection.GroupsResetting += (_, _) => resetting++;
        projection.GroupsReset += (_, _) => reset++;

        using (collection.DeferRefresh())
        {
            collection.ReplaceAll(CreateItems("new", 3));
        }

        resetting.Should().Be(1);
        reset.Should().Be(1);
    }

    [Fact]
    public void IncrementalAdd_ReusesExistingRows_AndDoesNotResetGroups()
    {
        var first = new TestItem("first", date: DateTimeOffset.Now);
        var collection = new MailListCollection<TestItem> { first };
        using var projection = new MailListProjection(
            collection,
            new MailListProjectionOptions { GroupMode = MailListGroupMode.None });
        var firstRow = projection.FindRow(first.StableId);

        var resets = 0;
        projection.GroupsResetting += (_, _) => resets++;

        collection.AddRange([new TestItem("second", date: DateTimeOffset.Now.AddMinutes(-1))]);

        resets.Should().Be(0);
        projection.FindRow(first.StableId).Should().BeSameAs(firstRow);
        projection.RowCount.Should().Be(2);
    }

    [Fact]
    public void ReplaceAll_PreservesExpansionForThreadsThatSurvive()
    {
        var collection = new MailListCollection<TestItem>();
        var firstHead = new TestItem("a1", "thread-a", DateTimeOffset.Now);
        var firstChild = new TestItem("a2", "thread-a", DateTimeOffset.Now.AddMinutes(-1));
        collection.AddRange([firstHead, firstChild]);
        using var projection = new MailListProjection(collection);
        projection.ExpandThread("thread-a");
        projection.RowCount.Should().Be(3);

        using (collection.DeferRefresh())
        {
            collection.ReplaceAll(
            [
                new TestItem("a1-reloaded", "thread-a", DateTimeOffset.Now),
                new TestItem("a2-reloaded", "thread-a", DateTimeOffset.Now.AddMinutes(-1)),
            ]);
        }

        projection.IsThreadExpanded("thread-a").Should().BeTrue();
        projection.RowCount.Should().Be(3);
    }

    [Fact]
    public void RowCount_TracksThreadExpansionAndCollapse()
    {
        var collection = new MailListCollection<TestItem>();
        collection.AddRange(
        [
            new TestItem("a1", "thread-a", DateTimeOffset.Now),
            new TestItem("a2", "thread-a", DateTimeOffset.Now.AddMinutes(-1)),
            new TestItem("single", date: DateTimeOffset.Now.AddMinutes(-2)),
        ]);
        using var projection = new MailListProjection(collection);

        projection.RowCount.Should().Be(2);

        projection.ExpandThread("thread-a");
        projection.RowCount.Should().Be(4);

        projection.CollapseThread("thread-a");
        projection.RowCount.Should().Be(2);
    }

    [Fact]
    public void ReplaceAll_KeepsOrderingForDateGroupingAndPinnedFirst()
    {
        var collection = new MailListCollection<TestItem>();
        collection.AddRange(CreateItems("old", 2));
        using var projection = new MailListProjection(collection);

        var pinned = new TestItem("pinned", date: DateTimeOffset.Now.AddDays(-3)) { IsPinned = true };
        var newest = new TestItem("newest", date: DateTimeOffset.Now);
        var oldest = new TestItem("oldest", date: DateTimeOffset.Now.AddDays(-1));

        using (collection.DeferRefresh())
        {
            collection.ReplaceAll([oldest, newest, pinned]);
        }

        projection.Groups[0].Key.Should().Be(new MailListProjectionGroupKey(true, null));
        projection.Rows.Select(row => row.SourceItem).Should().Equal(pinned, newest, oldest);
    }

    [Fact]
    public void ReplaceAll_KeepsOrderingWhenSortingByName()
    {
        var collection = new MailListCollection<TestItem>();
        collection.AddRange(CreateItems("old", 2));
        using var projection = new MailListProjection(
            collection,
            new MailListProjectionOptions
            {
                SortMode = MailListSortMode.Name,
                GroupMode = MailListGroupMode.Name,
            });

        var carol = new TestItem("carol");
        var alice = new TestItem("alice");
        var bob = new TestItem("bob");

        using (collection.DeferRefresh())
        {
            collection.ReplaceAll([carol, alice, bob]);
        }

        projection.Rows.Select(row => row.SourceItem).Should().Equal(alice, bob, carol);
    }

    [Fact]
    public void ReplaceAll_WithThreadingDisabled_ProducesOneRowPerItem()
    {
        var collection = new MailListCollection<TestItem>();
        collection.AddRange(CreateItems("old", 2));
        using var projection = new MailListProjection(
            collection,
            new MailListProjectionOptions { IsThreadingEnabled = false });

        using (collection.DeferRefresh())
        {
            collection.ReplaceAll(
            [
                new TestItem("a1", "thread-a", DateTimeOffset.Now),
                new TestItem("a2", "thread-a", DateTimeOffset.Now.AddMinutes(-1)),
            ]);
        }

        projection.RowCount.Should().Be(2);
        projection.Rows.Should().NotContain(row => row.IsThreadHead);
    }

    [Fact]
    public void ReplaceAll_WithNoItems_ClearsEveryGroup()
    {
        var collection = new MailListCollection<TestItem>();
        collection.AddRange(CreateItems("old", 4));
        using var projection = new MailListProjection(collection);

        using (collection.DeferRefresh())
        {
            collection.ReplaceAll([]);
        }

        projection.RowCount.Should().Be(0);
        projection.Groups.Should().BeEmpty();
    }

    [Fact]
    public void RemovingLeaf_FromExpandedThread_KeepsSurvivingRowInstances_AndRaisesOneRemove()
    {
        var now = DateTimeOffset.Now;
        var newest = new TestItem("newest", "thread", now);
        var middle = new TestItem("middle", "thread", now.AddMinutes(-5));
        var oldest = new TestItem("oldest", "thread", now.AddMinutes(-10));
        var collection = new MailListCollection<TestItem>();
        collection.AddRange([newest, middle, oldest]);
        using var projection = new MailListProjection(
            collection,
            new MailListProjectionOptions { GroupMode = MailListGroupMode.None });
        projection.ExpandThread("thread");

        var headBefore = projection.FindRow(newest.StableId);
        var oldestChildBefore = projection.Rows.Single(row => row.IsThreadChild && row.SourceItem == oldest);
        var threadBefore = projection.FindThread("thread");
        var rowChanges = new List<NotifyCollectionChangedEventArgs>();
        projection.Groups[0].CollectionChanged += (_, args) => rowChanges.Add(args);

        using (collection.DeferRefresh())
        {
            collection.RemoveById(middle.StableId);
        }

        rowChanges.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Remove);
        projection.FindThread("thread").Should().BeSameAs(threadBefore);
        projection.FindThread("thread")!.Items.Should().Equal(newest, oldest);
        projection.Rows.Should().Contain(headBefore!);
        projection.Rows.Should().Contain(oldestChildBefore);
        projection.IsThreadExpanded("thread").Should().BeTrue();
    }

    [Fact]
    public void RemovingRepresentative_ReplacesOnlyTheHeadRow()
    {
        var now = DateTimeOffset.Now;
        var newest = new TestItem("newest", "thread", now);
        var middle = new TestItem("middle", "thread", now.AddMinutes(-5));
        var oldest = new TestItem("oldest", "thread", now.AddMinutes(-10));
        var collection = new MailListCollection<TestItem>();
        collection.AddRange([newest, middle, oldest]);
        using var projection = new MailListProjection(
            collection,
            new MailListProjectionOptions { GroupMode = MailListGroupMode.None });
        projection.ExpandThread("thread");
        var childrenBefore = projection.Rows.Where(row => row.IsThreadChild && row.SourceItem != newest).ToArray();

        using (collection.DeferRefresh())
        {
            collection.RemoveById(newest.StableId);
        }

        var head = projection.Rows.Single(row => row.IsThreadHead);
        head.SourceItem.Should().BeSameAs(middle);
        projection.Rows.Where(row => row.IsThreadChild).Should().Equal(childrenBefore);
    }

    [Fact]
    public void RemovingLeaf_UntilOneRemains_ProjectsSingleRow()
    {
        var now = DateTimeOffset.Now;
        var newest = new TestItem("newest", "thread", now);
        var oldest = new TestItem("oldest", "thread", now.AddMinutes(-10));
        var collection = new MailListCollection<TestItem>();
        collection.AddRange([newest, oldest]);
        using var projection = new MailListProjection(collection);
        projection.ExpandThread("thread");

        using (collection.DeferRefresh())
        {
            collection.RemoveById(oldest.StableId);
        }

        projection.Rows.Should().ContainSingle().Which.Kind.Should().Be(MailListRowKind.Single);
        projection.FindThread("thread").Should().BeNull();
        projection.ExpandedThreadKeys.Should().BeEmpty();
    }

    [Fact]
    public void GetAdjacentVisibleItem_WalksAcrossGroups()
    {
        var today = DateTimeOffset.Now;
        var first = new TestItem("a", date: today);
        var second = new TestItem("b", date: today.AddDays(-1));
        var third = new TestItem("c", date: today.AddDays(-2));
        var collection = new MailListCollection<TestItem>();
        collection.AddRange([third, first, second]);
        using var projection = new MailListProjection(collection);

        projection.Groups.Should().HaveCount(3);
        projection.GetAdjacentVisibleItem(first.StableId).Should().BeSameAs(second);
        projection.GetAdjacentVisibleItem(second.StableId).Should().BeSameAs(third);
        projection.GetAdjacentVisibleItem(third.StableId).Should().BeNull();
        projection.GetAdjacentVisibleItem(second.StableId, -1).Should().BeSameAs(first);
        projection.GetRowAtVisibleIndex(2)!.SourceItem.Should().BeSameAs(third);
        projection.GetRowAtVisibleIndex(3).Should().BeNull();
    }

    private static TestItem[] CreateItems(string prefix, int count)
    {
        var items = new TestItem[count];
        for (var index = 0; index < count; index++)
        {
            items[index] = new TestItem(
                prefix + "-" + index,
                date: DateTimeOffset.Now.AddMinutes(-index));
        }

        return items;
    }

    private sealed class TestItem : IMailListSourceItem
    {
        private bool _isPinned;
        private string? _threadKey;

        public TestItem(
            string name,
            string? threadKey = null,
            DateTimeOffset? date = null,
            Guid? id = null)
        {
            StableId = id ?? Guid.NewGuid();
            NameSortKey = name;
            _threadKey = threadKey;
            DateSortKey = date ?? DateTimeOffset.Now;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid StableId { get; }

        public string? ThreadKey
        {
            get => _threadKey;
            set
            {
                if (_threadKey == value)
                {
                    return;
                }

                _threadKey = value;
                PropertyChanged?.Invoke(this, new(nameof(ThreadKey)));
            }
        }

        public DateTimeOffset DateSortKey { get; }

        public string NameSortKey { get; }

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned == value)
                {
                    return;
                }

                _isPinned = value;
                PropertyChanged?.Invoke(this, new(nameof(IsPinned)));
            }
        }
    }
}
