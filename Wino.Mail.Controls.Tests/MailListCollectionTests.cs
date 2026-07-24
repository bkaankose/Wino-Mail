using System.ComponentModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Wino.Mail.Controls.Core;
using Xunit;

namespace Wino.Mail.Controls.Tests;

public sealed class MailListCollectionTests
{
    [Fact]
    public void AddRange_IndexesItems_AndSkipsExistingIds()
    {
        var existing = new TestItem("a");
        var added = new TestItem("b");
        var collection = new MailListCollection<TestItem> { existing };

        collection.AddRange([existing, added]).Should().Be(1);

        collection.Count.Should().Be(2);
        collection.TryGetItem(added.StableId, out TestItem? resolved).Should().BeTrue();
        resolved.Should().BeSameAs(added);
    }

    [Fact]
    public void DuplicateIdentity_IsRejected()
    {
        var item = new TestItem("a");
        var duplicate = new TestItem("b", id: item.StableId);
        var collection = new MailListCollection<TestItem> { item };

        var act = () => collection.Add(duplicate);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RemoveRangeById_UpdatesIdentityIndex()
    {
        var first = new TestItem("a");
        var second = new TestItem("b");
        var collection = new MailListCollection<TestItem> { first, second };

        collection.RemoveRangeById([first.StableId]).Should().Be(1);

        collection.ContainsId(first.StableId).Should().BeFalse();
        collection.ContainsId(second.StableId).Should().BeTrue();
    }

    [Fact]
    public void RangeMutations_DoNotPublishCollectionReset()
    {
        var first = new TestItem("first");
        var second = new TestItem("second");
        var third = new TestItem("third");
        var collection = new MailListCollection<TestItem> { first };
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, args) => actions.Add(args.Action);

        collection.AddRange([second, third]);
        collection.RemoveRangeById([first.StableId, third.StableId]);

        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
        actions.Should().Contain(NotifyCollectionChangedAction.Add);
        actions.Should().Contain(NotifyCollectionChangedAction.Remove);
    }

    [Fact]
    public void Projection_ExpandsOnlyOneThread_AndPreservesLeafIdentity()
    {
        var collection = new MailListCollection<TestItem>();
        var firstA = new TestItem("a1", "thread-a", DateTimeOffset.Now);
        var secondA = new TestItem("a2", "thread-a", DateTimeOffset.Now.AddMinutes(-1));
        var firstB = new TestItem("b1", "thread-b", DateTimeOffset.Now.AddMinutes(-2));
        var secondB = new TestItem("b2", "thread-b", DateTimeOffset.Now.AddMinutes(-3));
        collection.AddRange([firstA, secondA, firstB, secondB]);
        using var projection = new MailListProjection(collection);

        projection.ExpandThread("thread-a");
        projection.Rows.Count(row => row.ThreadKey == "thread-a").Should().Be(3);

        projection.ExpandThread("thread-b");

        projection.IsThreadExpanded("thread-a").Should().BeFalse();
        projection.IsThreadExpanded("thread-b").Should().BeTrue();
        projection.FindItem(firstA.StableId).Should().BeSameAs(firstA);
    }

    [Fact]
    public void Projection_ExpansionPreservesUnrelatedRowInstances()
    {
        var single = new TestItem("single", date: DateTimeOffset.Now);
        var first = new TestItem("first", "thread", DateTimeOffset.Now.AddMinutes(-1));
        var second = new TestItem("second", "thread", DateTimeOffset.Now.AddMinutes(-2));
        var collection = new MailListCollection<TestItem> { single, first, second };
        using var projection = new MailListProjection(collection);
        var singleRow = projection.FindRow(single.StableId);
        var threadHead = projection.Rows.Single(row => row.IsThreadHead);
        var expansionNotifications = 0;
        threadHead.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MailListRow.IsExpanded))
            {
                expansionNotifications++;
            }
        };

        projection.ExpandThread("thread");

        projection.FindRow(single.StableId).Should().BeSameAs(singleRow);
        projection.Rows.Single(row => row.IsThreadHead).Should().BeSameAs(threadHead);

        projection.CollapseThread("thread");

        projection.FindRow(single.StableId).Should().BeSameAs(singleRow);
        projection.Rows.Single(row => row.IsThreadHead).Should().BeSameAs(threadHead);
        expansionNotifications.Should().Be(2);
    }

    [Fact]
    public void Projection_AddAndRemovePreserveUnaffectedGroupsAndRows()
    {
        var now = DateTimeOffset.Now;
        var first = new TestItem("first", date: now);
        var second = new TestItem("second", date: now.AddMinutes(-1));
        var collection = new MailListCollection<TestItem> { first, second };
        using var projection = new MailListProjection(collection);
        var existingGroup = projection.Groups.Single();
        var firstRow = projection.FindRow(first.StableId);
        var secondRow = projection.FindRow(second.StableId);
        var added = new TestItem("added", date: now.AddSeconds(1));

        collection.Add(added);

        projection.Groups.Single().Should().BeSameAs(existingGroup);
        projection.FindRow(first.StableId).Should().BeSameAs(firstRow);
        projection.FindRow(second.StableId).Should().BeSameAs(secondRow);

        collection.RemoveById(added.StableId);

        projection.Groups.Single().Should().BeSameAs(existingGroup);
        projection.FindRow(first.StableId).Should().BeSameAs(firstRow);
        projection.FindRow(second.StableId).Should().BeSameAs(secondRow);
    }

    [Fact]
    public void Projection_RemovalPreservesRelativeOrderForEqualSortKeys()
    {
        var date = DateTimeOffset.Now;
        var first = new TestItem("first", date: date);
        var removed = new TestItem("removed", date: date);
        var second = new TestItem("second", date: date);
        var third = new TestItem("third", date: date);
        var collection = new MailListCollection<TestItem>
        {
            first,
            removed,
            second,
            third,
        };
        using var projection = new MailListProjection(collection);

        collection.RemoveById(removed.StableId);

        projection.Rows
            .Select(row => row.SourceItem)
            .Should()
            .Equal(first, second, third);
    }

    [Fact]
    public void Projection_ReordersWhenPinMetadataChanges()
    {
        var older = new TestItem("older", date: DateTimeOffset.Now.AddDays(-1));
        var newer = new TestItem("newer", date: DateTimeOffset.Now);
        var collection = new MailListCollection<TestItem> { older, newer };
        using var projection = new MailListProjection(collection);

        older.IsPinned = true;

        projection.Rows.First().SourceItem.Should().BeSameAs(older);
    }

    [Fact]
    public void Projection_CoalescesStructuralChangesDuringCollectionBatch()
    {
        var first = new TestItem("first", "thread-a");
        var second = new TestItem("second", "thread-b");
        var collection = new MailListCollection<TestItem> { first, second };
        using var projection = new MailListProjection(collection);
        var projectionChanges = 0;
        projection.ProjectionChanged += (_, _) => projectionChanges++;

        using (collection.DeferRefresh())
        {
            first.ThreadKey = "shared";
            second.ThreadKey = "shared";
        }

        projectionChanges.Should().Be(1);
        projection.Rows.Should().ContainSingle(row => row.IsThreadHead);
    }

    [Fact]
    public void Projection_CreatesDedicatedPinnedGroup()
    {
        var pinned = new TestItem("pinned") { IsPinned = true };
        var regular = new TestItem("regular");
        var collection = new MailListCollection<TestItem> { regular, pinned };
        using var projection = new MailListProjection(collection);

        projection.Groups.Should().HaveCount(2);
        projection.Groups[0].Key.Should().Be(
            new MailListProjectionGroupKey(true, null));
    }

    private sealed class TestItem : IMailListSourceItem
    {
        private bool _isPinned;

        public TestItem(
            string name,
            string? threadKey = null,
            DateTimeOffset? date = null,
            Guid? id = null)
        {
            StableId = id ?? Guid.NewGuid();
            NameSortKey = name;
            ThreadKey = threadKey;
            DateSortKey = date ?? DateTimeOffset.Now;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid StableId { get; }

        private string? _threadKey;

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
