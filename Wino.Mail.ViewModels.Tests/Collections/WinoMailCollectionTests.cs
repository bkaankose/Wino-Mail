using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
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

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
