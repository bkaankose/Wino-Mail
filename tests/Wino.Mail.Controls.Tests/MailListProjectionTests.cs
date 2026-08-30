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
    public void IsPointerOver_NotifiesOnce_PerStateChange()
    {
        var item = new TestItem("single");
        var collection = new MailListCollection<TestItem> { item };
        using var projection = new MailListProjection(collection);
        var row = projection.FindRow(item.StableId)!;
        var notifications = 0;
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MailListRow.IsPointerOver))
            {
                notifications++;
            }
        };

        row.IsPointerOver = true;
        row.IsPointerOver = true;
        row.IsPointerOver = false;

        notifications.Should().Be(2);
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
