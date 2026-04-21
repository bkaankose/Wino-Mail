using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests.Collections;

public class WinoMailCollectionTests
{
    [Fact]
    public async Task AddAsync_ShouldAddSingleItemAsMailItemViewModel()
    {
        var sut = CreateCollection();
        var mail = CreateMailCopy(threadId: "thread-1");

        await sut.AddAsync(mail);

        var items = FlattenItems(sut);
        items.Should().ContainSingle().Which.Should().BeOfType<MailItemViewModel>();
        sut.ContainsMailUniqueId(mail.UniqueId).Should().BeTrue();
    }

    [Fact]
    public async Task AddAsync_ShouldKeepItemsSeparate_WhenThreadIdsDiffer()
    {
        var sut = CreateCollection();
        var first = CreateMailCopy(threadId: "thread-1");
        var second = CreateMailCopy(threadId: "thread-2");

        await sut.AddAsync(first);
        await sut.AddAsync(second);

        var items = FlattenItems(sut);
        items.Should().HaveCount(2);
        items.Should().OnlyContain(item => item is MailItemViewModel);
    }

    [Fact]
    public async Task AddAsync_ShouldConvertSingleItemToThread_WhenSecondItemWithSameThreadIdIsAdded()
    {
        var sut = CreateCollection();
        var first = CreateMailCopy(threadId: "shared-thread", creationDate: DateTime.UtcNow.AddMinutes(-1));
        var second = CreateMailCopy(threadId: "shared-thread", creationDate: DateTime.UtcNow);

        await sut.AddAsync(first);
        FlattenItems(sut).Should().ContainSingle().Which.Should().BeOfType<MailItemViewModel>();

        await sut.AddAsync(second);

        var items = FlattenItems(sut);
        var threadItem = items.Should().ContainSingle().Which.Should().BeOfType<ThreadMailItemViewModel>().Subject;
        threadItem.EmailCount.Should().Be(2);
        threadItem.GetContainingIds().Should().BeEquivalentTo([first.UniqueId, second.UniqueId]);
    }

    [Fact]
    public async Task RemoveAsync_ShouldConvertThreadToSingleItem_WhenThreadDropsToOneItem()
    {
        var sut = CreateCollection();
        var first = CreateMailCopy(threadId: "shared-thread", creationDate: DateTime.UtcNow.AddMinutes(-1));
        var second = CreateMailCopy(threadId: "shared-thread", creationDate: DateTime.UtcNow);

        await sut.AddAsync(first);
        await sut.AddAsync(second);

        await sut.RemoveAsync(second);

        var items = FlattenItems(sut);
        var remainingItem = items.Should().ContainSingle().Which.Should().BeOfType<MailItemViewModel>().Subject;
        remainingItem.MailCopy.UniqueId.Should().Be(first.UniqueId);

        var container = sut.GetMailItemContainer(first.UniqueId);
        container.Should().NotBeNull();
        container.ThreadViewModel.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_ShouldPruneRemainingNonDraftSingle_WhenDraftPruningIsEnabled()
    {
        var sut = CreateCollection();
        sut.PruneSingleNonDraftItems = true;

        var nonDraft = CreateMailCopy(threadId: "shared-thread", creationDate: DateTime.UtcNow.AddMinutes(-1));
        var draft = CreateMailCopy(threadId: "shared-thread", creationDate: DateTime.UtcNow);
        draft.IsDraft = true;
        draft.AssignedFolder = new MailItemFolder { SpecialFolderType = SpecialFolderType.Draft };

        await sut.AddAsync(nonDraft);
        await sut.AddAsync(draft);

        await sut.RemoveAsync(draft);

        FlattenItems(sut).Should().BeEmpty();
        sut.ContainsMailUniqueId(nonDraft.UniqueId).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemoveSingleItem()
    {
        var sut = CreateCollection();
        var mail = CreateMailCopy(threadId: "thread-1");

        await sut.AddAsync(mail);
        await sut.RemoveAsync(mail);

        FlattenItems(sut).Should().BeEmpty();
        sut.ContainsMailUniqueId(mail.UniqueId).Should().BeFalse();
    }

    [Fact]
    public async Task AddRangeAsync_ShouldCreateThreadsForItemsWithMatchingThreadId()
    {
        var sut = CreateCollection();

        var threadAFirst = new MailItemViewModel(CreateMailCopy(threadId: "thread-a", creationDate: DateTime.UtcNow.AddMinutes(-10)));
        var threadASecond = new MailItemViewModel(CreateMailCopy(threadId: "thread-a", creationDate: DateTime.UtcNow.AddMinutes(-9)));
        var threadCFirst = new MailItemViewModel(CreateMailCopy(threadId: "thread-c", creationDate: DateTime.UtcNow.AddMinutes(-8)));
        var threadCSecond = new MailItemViewModel(CreateMailCopy(threadId: "thread-c", creationDate: DateTime.UtcNow.AddMinutes(-7)));
        var single = new MailItemViewModel(CreateMailCopy(threadId: "thread-b", creationDate: DateTime.UtcNow.AddMinutes(-6)));

        await sut.AddRangeAsync([threadAFirst, threadASecond, threadCFirst, threadCSecond, single], clearIdCache: true);

        var items = FlattenItems(sut);
        items.Should().HaveCount(3);
        items.Count(item => item is ThreadMailItemViewModel).Should().Be(2);
        items.Count(item => item is MailItemViewModel).Should().Be(1);

        var threadItems = items.OfType<ThreadMailItemViewModel>().ToList();
        threadItems.Should().Contain(item => item.ThreadId == "thread-a" && item.EmailCount == 2);
        threadItems.Should().Contain(item => item.ThreadId == "thread-c" && item.EmailCount == 2);
    }

    [Fact]
    public async Task AddRangeAsync_ShouldKeepGroupsAndItemsSortedDuringHighVolumeInitialization()
    {
        var sut = CreateCollection();
        var baseDate = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc);

        var items = Enumerable.Range(0, 240)
            .Select(index =>
            {
                var dayOffset = index % 4;
                var minuteOffset = 240 - index;

                return new MailItemViewModel(CreateMailCopy(
                    threadId: $"single-{index}",
                    creationDate: baseDate.AddDays(-dayOffset).AddMinutes(-minuteOffset)));
            })
            .OrderByDescending(item => item.MailCopy.UniqueId)
            .ToList();

        await sut.AddRangeAsync(items, clearIdCache: true);

        var groups = new List<(DateTime Key, List<IMailListItem> Items)>();
        foreach (var group in sut.MailItems)
        {
            var groupItems = new List<IMailListItem>();
            foreach (var item in group)
            {
                groupItems.Add(item);
            }

            groups.Add((((MailListGroupKey)group.Key).Value is DateTime keyDate ? keyDate : default, groupItems));
        }

        groups.Should().NotBeEmpty();

        var orderedGroupKeys = groups.Select(group => group.Key).ToList();
        orderedGroupKeys.Should().BeInDescendingOrder();

        foreach (var group in groups)
        {
            group.Items.Should().OnlyContain(item => item is MailItemViewModel);

            var creationDates = group.Items
                .Cast<MailItemViewModel>()
                .Select(item => item.MailCopy.CreationDate)
                .ToList();

            creationDates.Should().BeInDescendingOrder();
        }
    }

    [Fact]
    public async Task AddRangeAsync_ShouldPlacePinnedItemsBeforeUnpinnedItems()
    {
        var sut = CreateCollection();
        var olderPinned = CreateMailCopy(threadId: "pinned", creationDate: DateTime.UtcNow.AddDays(-3));
        olderPinned.IsPinned = true;

        var newerUnpinned = CreateMailCopy(threadId: "regular", creationDate: DateTime.UtcNow);

        await sut.AddRangeAsync(
            [
                new MailItemViewModel(newerUnpinned),
                new MailItemViewModel(olderPinned)
            ],
            clearIdCache: true);

        var firstItem = FlattenItems(sut).First().Should().BeOfType<MailItemViewModel>().Subject;
        firstItem.MailCopy.UniqueId.Should().Be(olderPinned.UniqueId);
    }

    [Fact]
    public async Task UpdateMailCopy_ShouldMovePinnedItemToTop()
    {
        var sut = CreateCollection();
        var older = CreateMailCopy(threadId: "older", creationDate: DateTime.UtcNow.AddDays(-2));
        var newer = CreateMailCopy(threadId: "newer", creationDate: DateTime.UtcNow);

        await sut.AddAsync(older);
        await sut.AddAsync(newer);

        var updatedOlder = CloneMailCopy(older);
        updatedOlder.IsPinned = true;

        await sut.UpdateMailCopy(updatedOlder, EntityUpdateSource.Server, MailCopyChangeFlags.IsPinned);

        var firstItem = FlattenItems(sut).First().Should().BeOfType<MailItemViewModel>().Subject;
        firstItem.MailCopy.UniqueId.Should().Be(older.UniqueId);
    }

    [Fact]
    public async Task UpdateMailCopy_ShouldMergeExistingSingles_WhenThreadIdChangesToMatch()
    {
        var sut = CreateCollection();
        var first = CreateMailCopy(threadId: "shared-thread", creationDate: DateTime.UtcNow.AddMinutes(-1));
        var second = CreateMailCopy(threadId: string.Empty, creationDate: DateTime.UtcNow);

        await sut.AddAsync(first);
        await sut.AddAsync(second);

        var updatedSecond = CloneMailCopy(second);
        updatedSecond.ThreadId = "shared-thread";

        await sut.UpdateMailCopy(updatedSecond, EntityUpdateSource.Server, MailCopyChangeFlags.ThreadId);

        var items = FlattenItems(sut);
        var threadItem = items.Should().ContainSingle().Which.Should().BeOfType<ThreadMailItemViewModel>().Subject;
        threadItem.EmailCount.Should().Be(2);
        threadItem.GetContainingIds().Should().BeEquivalentTo([first.UniqueId, second.UniqueId]);
    }

    [Fact]
    public async Task AddAsync_ShouldThreadWithUpdatedItem_WhenThreadIdWasSetByPriorUpdate()
    {
        var sut = CreateCollection();
        var existing = CreateMailCopy(threadId: string.Empty, creationDate: DateTime.UtcNow.AddMinutes(-1));
        var incoming = CreateMailCopy(threadId: "shared-thread", creationDate: DateTime.UtcNow);

        await sut.AddAsync(existing);

        var updatedExisting = CloneMailCopy(existing);
        updatedExisting.ThreadId = "shared-thread";

        await sut.UpdateMailCopy(updatedExisting, EntityUpdateSource.Server, MailCopyChangeFlags.ThreadId);
        await sut.AddAsync(incoming);

        var items = FlattenItems(sut);
        var threadItem = items.Should().ContainSingle().Which.Should().BeOfType<ThreadMailItemViewModel>().Subject;
        threadItem.EmailCount.Should().Be(2);
        threadItem.GetContainingIds().Should().BeEquivalentTo([existing.UniqueId, incoming.UniqueId]);
    }

    [Fact]
    public async Task AddAsync_ShouldRemainConsistentUnderHighVolumeConcurrentAdds()
    {
        var sut = CreateCollection();
        var threadCount = 40;
        var mailsPerThread = 25;
        var baseDate = new DateTime(2026, 4, 6, 12, 0, 0, DateTimeKind.Utc);

        var mails = Enumerable.Range(0, threadCount)
            .SelectMany(threadIndex => Enumerable.Range(0, mailsPerThread)
                .Select(mailIndex => CreateMailCopy(
                    threadId: $"thread-{threadIndex}",
                    creationDate: baseDate.AddMinutes(-(threadIndex * mailsPerThread + mailIndex)))))
            .OrderBy(_ => Guid.NewGuid())
            .ToList();

        await Task.WhenAll(mails.Select(mail => Task.Run(() => sut.AddAsync(mail))));

        var flattenedMailIds = FlattenMailItems(sut)
            .Select(item => item.MailCopy.UniqueId)
            .ToList();

        flattenedMailIds.Should().HaveCount(threadCount * mailsPerThread);
        flattenedMailIds.Should().OnlyHaveUniqueItems();
        flattenedMailIds.Should().BeEquivalentTo(mails.Select(mail => mail.UniqueId));

        var topLevelItems = FlattenItems(sut);
        topLevelItems.Should().HaveCount(threadCount);
        topLevelItems.Should().OnlyContain(item => item is ThreadMailItemViewModel);

        foreach (var thread in topLevelItems.Cast<ThreadMailItemViewModel>())
        {
            thread.EmailCount.Should().Be(mailsPerThread);

            var expectedIds = mails
                .Where(mail => mail.ThreadId == thread.ThreadId)
                .Select(mail => mail.UniqueId);

            thread.GetContainingIds().Should().BeEquivalentTo(expectedIds);
        }
    }

    [Fact]
    public async Task ExecuteSelectionBatchAsync_ShouldRaiseSelectionChangedOnce()
    {
        var sut = CreateCollection();
        var first = CreateMailCopy(threadId: "thread-1");
        var second = CreateMailCopy(threadId: "thread-2");

        await sut.AddAsync(first);
        await sut.AddAsync(second);

        var items = FlattenMailItems(sut);
        var eventCount = 0;
        sut.ItemSelectionChanged += (_, _) => eventCount++;

        await sut.ExecuteSelectionBatchAsync(() =>
        {
            items[0].IsSelected = true;
            items[1].IsSelected = true;
            items[0].IsSelected = false;
        });

        eventCount.Should().Be(1);
        sut.SelectedItems.Should().ContainSingle();
    }

    private static WinoMailCollection CreateCollection() => new()
    {
        CoreDispatcher = new ImmediateDispatcher()
    };

    private static List<IMailListItem> FlattenItems(WinoMailCollection collection)
    {
        var items = new List<IMailListItem>();

        foreach (var group in collection.MailItems)
        {
            foreach (var item in group)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static List<MailItemViewModel> FlattenMailItems(WinoMailCollection collection)
    {
        var items = new List<MailItemViewModel>();

        foreach (var group in collection.MailItems)
        {
            foreach (var item in group)
            {
                if (item is MailItemViewModel mailItem)
                {
                    items.Add(mailItem);
                }
                else if (item is ThreadMailItemViewModel threadItem)
                {
                    items.AddRange(threadItem.ThreadEmails);
                }
            }
        }

        return items;
    }

    private static MailCopy CreateMailCopy(string threadId, DateTime? creationDate = null)
        => new()
        {
            UniqueId = Guid.NewGuid(),
            ThreadId = threadId,
            CreationDate = creationDate ?? DateTime.UtcNow,
            FromName = "Sender",
            FromAddress = "sender@wino.dev",
            Subject = "Subject",
            PreviewText = "Preview",
            FileId = Guid.NewGuid(),
            FolderId = Guid.NewGuid()
        };

    private static MailCopy CloneMailCopy(MailCopy source)
        => new()
        {
            UniqueId = source.UniqueId,
            Id = source.Id,
            FolderId = source.FolderId,
            ThreadId = source.ThreadId,
            MessageId = source.MessageId,
            References = source.References,
            InReplyTo = source.InReplyTo,
            FromName = source.FromName,
            FromAddress = source.FromAddress,
            Subject = source.Subject,
            PreviewText = source.PreviewText,
            CreationDate = source.CreationDate,
            Importance = source.Importance,
            IsRead = source.IsRead,
            IsFlagged = source.IsFlagged,
            IsPinned = source.IsPinned,
            IsFocused = source.IsFocused,
            HasAttachments = source.HasAttachments,
            ItemType = source.ItemType,
            DraftId = source.DraftId,
            IsDraft = source.IsDraft,
            FileId = source.FileId,
            SenderContact = source.SenderContact,
            AssignedAccount = source.AssignedAccount,
            AssignedFolder = source.AssignedFolder
        };

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
