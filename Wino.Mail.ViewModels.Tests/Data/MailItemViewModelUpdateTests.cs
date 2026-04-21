using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests.Data;

public class MailItemViewModelUpdateTests
{
    [Fact]
    public void UpdateFrom_ShouldNotifyOnlyReadState_WhenSameInstanceAndHintProvided()
    {
        var mailCopy = CreateMailCopy("thread-1", DateTime.UtcNow);
        var sut = new MailItemViewModel(mailCopy);
        var raisedProperties = new List<string>();

        sut.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                raisedProperties.Add(e.PropertyName);
            }
        };

        mailCopy.IsRead = true;

        sut.UpdateFrom(mailCopy, MailCopyChangeFlags.IsRead);

        raisedProperties.Should().Equal(nameof(MailItemViewModel.IsRead));
    }

    [Fact]
    public void UpdateFrom_ShouldNotifyAddressAndDependentSenderFields_WhenFromAddressChanges()
    {
        var original = CreateMailCopy("thread-1", DateTime.UtcNow);
        original.FromName = string.Empty;
        var updated = CloneMailCopy(original);
        updated.FromAddress = "updated@wino.dev";

        var sut = new MailItemViewModel(original);
        var raisedProperties = new List<string>();

        sut.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                raisedProperties.Add(e.PropertyName);
            }
        };

        sut.UpdateFrom(updated);

        raisedProperties.Should().Equal(
            nameof(MailItemViewModel.FromAddress),
            nameof(MailItemViewModel.FromName),
            nameof(MailItemViewModel.SortingName));
    }

    [Fact]
    public void UpdateFrom_ShouldNotifyPinnedState_WhenPinnedChanges()
    {
        var original = CreateMailCopy("thread-1", DateTime.UtcNow);
        var updated = CloneMailCopy(original);
        updated.IsPinned = true;

        var sut = new MailItemViewModel(original);
        var raisedProperties = new List<string>();

        sut.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                raisedProperties.Add(e.PropertyName);
            }
        };

        sut.UpdateFrom(updated);

        raisedProperties.Should().Contain(nameof(MailItemViewModel.IsPinned));
    }

    [Fact]
    public async Task UpdateMailCopy_ShouldNotifyThreadOnlyForReadState_WhenReadStateChanges()
    {
        var collection = new WinoMailCollection
        {
            CoreDispatcher = new ImmediateDispatcher()
        };

        var older = CreateMailCopy("thread-1", DateTime.UtcNow.AddMinutes(-5));
        var latest = CreateMailCopy("thread-1", DateTime.UtcNow);

        await collection.AddAsync(older);
        await collection.AddAsync(latest);

        ThreadMailItemViewModel? threadItem = null;
        foreach (var group in collection.MailItems)
        {
            foreach (var item in group)
            {
                if (item is ThreadMailItemViewModel thread)
                {
                    threadItem = thread;
                    break;
                }
            }

            if (threadItem != null)
                break;
        }

        threadItem.Should().NotBeNull();

        var raisedProperties = new List<string>();
        threadItem!.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                raisedProperties.Add(e.PropertyName);
            }
        };

        latest.IsRead = true;

        await collection.UpdateMailCopy(latest, EntityUpdateSource.ClientUpdated, MailCopyChangeFlags.IsRead);

        raisedProperties.Should().Equal(nameof(ThreadMailItemViewModel.IsRead));
    }

    private static MailCopy CreateMailCopy(string threadId, DateTime creationDate)
        => new()
        {
            UniqueId = Guid.NewGuid(),
            Id = Guid.NewGuid().ToString("N"),
            FolderId = Guid.NewGuid(),
            ThreadId = threadId,
            MessageId = $"message-{Guid.NewGuid():N}",
            References = string.Empty,
            InReplyTo = string.Empty,
            FromName = "Sender",
            FromAddress = "sender@wino.dev",
            Subject = "Subject",
            PreviewText = "Preview",
            CreationDate = creationDate,
            Importance = MailImportance.Normal,
            IsRead = false,
            IsFlagged = false,
            IsPinned = false,
            IsFocused = false,
            HasAttachments = false,
            ItemType = MailItemType.Mail,
            DraftId = string.Empty,
            IsDraft = false,
            FileId = Guid.NewGuid()
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
