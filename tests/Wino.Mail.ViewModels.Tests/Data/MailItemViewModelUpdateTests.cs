using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Controls.Core.IntelligenceTileBar;
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

    [Fact]
    public void IntelligenceTiles_ShouldApplyVisibilityPolicyAndPreserveLabelOrder()
    {
        var mail = CreateMailCopy("thread-1", DateTime.UtcNow);
        mail.IntelligenceMetadata = new MailIntelligenceMetadata(
            "outlook:test",
            [
                new SmartLabelScore(MailSmartLabel.Travel, 0.9),
                new SmartLabelScore(MailSmartLabel.Finance, 0.8),
            ],
            null,
            "Review the attached contract by Friday.");

        var sut = new MailItemViewModel(mail);

        sut.IntelligenceTiles.Select(static tile => tile.Kind).Should().Equal(
            WinoIntelligenceTileKind.SmartLabel,
            WinoIntelligenceTileKind.SmartLabel);
        sut.IntelligenceTiles.Select(static tile => tile.Text).Should().Equal(
            "IntelligenceTile_LabelTravel",
            "IntelligenceTile_LabelFinance");
    }

    [Fact]
    public void ThreadIntelligenceTiles_ShouldComeOnlyFromNewestMessage()
    {
        var older = CreateMailCopy("thread-1", DateTime.UtcNow.AddMinutes(-5));
        older.IntelligenceMetadata = CreatePriorityMetadata(MailPriority.Urgent);
        var newest = CreateMailCopy("thread-1", DateTime.UtcNow);
        newest.IntelligenceMetadata = CreatePriorityMetadata(MailPriority.High);
        var sut = new ThreadMailItemViewModel("thread-1", isNewestEmailFirst: true);
        sut.AddEmail(new MailItemViewModel(older));
        sut.AddEmail(new MailItemViewModel(newest));

        sut.IntelligenceTiles.Where(static tile => tile.Kind == WinoIntelligenceTileKind.Priority)
            .Should().ContainSingle().Which.Text.Should().Be("IntelligenceTile_PriorityHigh");
    }

    private static MailIntelligenceMetadata CreatePriorityMetadata(MailPriority priority)
        => new("outlook:test", [], new GeneralFactPayload
        {
            BriefingId = Guid.NewGuid(),
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Kind = MessageKind.Information,
            Status = BriefingStatus.Informational,
            Urgency = priority,
            PrimaryAction = new NoActionPayload(),
            TemporalReferences = [],
            Confidence = 0.9,
        }, string.Empty);

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
            AssignedFolder = source.AssignedFolder,
            IntelligenceMetadata = source.IntelligenceMetadata
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
