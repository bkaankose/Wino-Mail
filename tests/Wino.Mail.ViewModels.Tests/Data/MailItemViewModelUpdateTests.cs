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
            nameof(MailItemViewModel.SortingName),
            nameof(MailItemViewModel.NameSortKey));
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
    public async Task UpdateMailCopy_ShouldNotifyOnlyAffectedLeafForReadState()
    {
        var collection = new MailListStore
        {
            CoreDispatcher = new ImmediateDispatcher()
        };

        var older = CreateMailCopy("thread-1", DateTime.UtcNow.AddMinutes(-5));
        var latest = CreateMailCopy("thread-1", DateTime.UtcNow);

        await collection.AddAsync(older);
        await collection.AddAsync(latest);

        var leaf = collection.Find(latest.UniqueId);
        leaf.Should().NotBeNull();

        var raisedProperties = new List<string>();
        leaf!.PropertyChanged += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
            {
                raisedProperties.Add(e.PropertyName);
            }
        };

        latest.IsRead = true;

        await collection.UpdateMailCopy(latest, EntityUpdateSource.ClientUpdated, MailCopyChangeFlags.IsRead);

        raisedProperties.Should().Contain(nameof(MailItemViewModel.IsRead));
    }

    /// <summary>
    /// CreationDate is stored as UTC everywhere, but the two ways a mail reaches the list disagree on
    /// its DateTimeKind: sqlite returns Unspecified on a folder load, while a freshly synced copy is
    /// still Utc. new DateTimeOffset(DateTime) reads Unspecified as local time, so the sort key used
    /// to differ by the local UTC offset between those two paths and a newly arrived mail sorted into
    /// the middle of the list until the folder was reloaded.
    ///
    /// Note this assertion can only fail on a machine that is not at UTC, because at UTC the two
    /// interpretations coincide. Running the suite in a non-UTC zone is what actually protects this.
    /// </summary>
    [Fact]
    public void DateSortKey_ForUnspecifiedKind_IsInterpretedAsUtc()
    {
        var creationDate = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified);
        var sut = new MailItemViewModel(CreateMailCopy("thread-1", creationDate));

        sut.DateSortKey.UtcDateTime.Should().Be(DateTime.SpecifyKind(creationDate, DateTimeKind.Utc));
    }

    [Fact]
    public void DateSortKey_ForUnspecifiedAndUtcKind_ProducesTheSameInstant()
    {
        var ticks = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Unspecified);

        var fromDatabase = new MailItemViewModel(CreateMailCopy("thread-1", ticks));
        var fromSynchronizer = new MailItemViewModel(
            CreateMailCopy("thread-1", DateTime.SpecifyKind(ticks, DateTimeKind.Utc)));

        fromDatabase.DateSortKey.Should().Be(fromSynchronizer.DateSortKey,
            "a mail loaded from the database and the same mail arriving live must sort identically");
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
