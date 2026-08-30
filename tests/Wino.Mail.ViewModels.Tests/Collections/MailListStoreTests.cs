using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Mail.ViewModels.Tests.Collections;

public sealed class MailListStoreTests
{
    [Fact]
    public async Task AddAsync_KeepsThreadLeavesFlat()
    {
        var store = CreateStore();

        await store.AddAsync(CreateMailCopy("thread-1"));
        await store.AddAsync(CreateMailCopy("thread-1"));

        ((IEnumerable<MailItemViewModel>)store.Items).Should().HaveCount(2);
        using var projection = new MailListProjection(store.Items);
        projection.Rows.Should().ContainSingle(row => row.IsThreadHead);
    }

    [Fact]
    public async Task AddAsync_UpdatesExistingIdentityWithoutReset()
    {
        var store = CreateStore();
        var original = CreateMailCopy("thread-1");
        await store.AddAsync(original);
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        store.Items.CollectionChanged += (_, args) => collectionChanges.Add(args.Action);
        var updated = CloneMailCopy(original);
        updated.Subject = "Updated";

        await store.AddAsync(updated);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().ContainSingle();
        store.Find(original.UniqueId).Subject.Should().Be("Updated");
        collectionChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task AddRangeAsync_PublishesOneBatchedAdd()
    {
        var store = CreateStore();
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        store.Items.CollectionChanged += (_, args) => collectionChanges.Add(args.Action);

        await store.AddRangeAsync(
        [
            new MailItemViewModel(CreateMailCopy("thread-1")),
            new MailItemViewModel(CreateMailCopy("thread-2")),
            new MailItemViewModel(CreateMailCopy("thread-3")),
        ], clearIdCache: false);

        collectionChanges.Should().Equal(NotifyCollectionChangedAction.Add);
    }

    [Fact]
    public async Task StructuralBatch_RebuildsProjectionOnce()
    {
        var store = CreateStore();
        var first = CreateMailCopy("thread-1");
        var second = CreateMailCopy("thread-2");
        await store.AddRangeAsync(
        [
            new MailItemViewModel(first),
            new MailItemViewModel(second),
        ], clearIdCache: true);
        using var projection = new MailListProjection(store.Items);
        var projectionChanges = 0;
        projection.ProjectionChanged += (_, _) => projectionChanges++;
        var updatedFirst = CloneMailCopy(first);
        updatedFirst.ThreadId = "shared";
        var updatedSecond = CloneMailCopy(second);
        updatedSecond.ThreadId = "shared";

        await store.UpdateMailCopiesAsync(
            [updatedFirst, updatedSecond],
            EntityUpdateSource.Server,
            MailCopyChangeFlags.ThreadId);

        projectionChanges.Should().Be(1);
        projection.Rows.Should().ContainSingle(row => row.IsThreadHead);
    }

    [Fact]
    public async Task UpdateMailStatesAsync_DispatchesOneBatchedUiMutation()
    {
        var dispatcher = new RecordingDispatcher();
        var store = new MailListStore
        {
            CoreDispatcher = dispatcher,
        };
        var mails = Enumerable.Range(0, 100)
            .Select(_ => CreateMailCopy("thread"))
            .ToArray();
        await store.AddRangeAsync(
            mails.Select(static mail => new MailItemViewModel(mail)),
            clearIdCache: true);
        dispatcher.ExecutionCount = 0;

        await store.UpdateMailStatesAsync(
            mails.Select(static mail => new MailStateChange(mail.UniqueId, IsRead: true)),
            EntityUpdateSource.Server);

        dispatcher.ExecutionCount.Should().Be(1);
        ((IEnumerable<MailItemViewModel>)store.Items).Should().OnlyContain(static item => item.IsRead);
    }

    [Fact]
    public async Task AddRangeAsync_WhenRequestIsStale_DoesNotPublish()
    {
        var store = CreateStore();
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        store.Items.CollectionChanged += (_, args) => collectionChanges.Add(args.Action);

        await store.AddRangeAsync(
        [
            new MailItemViewModel(CreateMailCopy("stale-thread")),
        ], clearIdCache: true, shouldApply: static () => false);

        store.Count.Should().Be(0);
        collectionChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task RemovingThreadLeaf_NaturallyProjectsRemainingLeafAsSingle()
    {
        var store = CreateStore();
        var first = CreateMailCopy("thread-1");
        var second = CreateMailCopy("thread-1");
        await store.AddRangeAsync(
        [
            new MailItemViewModel(first),
            new MailItemViewModel(second),
        ], clearIdCache: true);
        using var projection = new MailListProjection(store.Items);

        await store.RemoveAsync(second);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().ContainSingle();
        projection.Rows.Should().ContainSingle()
            .Which.Kind.Should().Be(MailListRowKind.Single);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllIdentities()
    {
        var store = CreateStore();
        await store.AddRangeAsync(
        [
            new MailItemViewModel(CreateMailCopy("thread-1")),
            new MailItemViewModel(CreateMailCopy("thread-2")),
        ], clearIdCache: true);

        await store.ClearAsync();

        ((IEnumerable<MailItemViewModel>)store.Items).Should().BeEmpty();
        store.ItemIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetAsync_ReplacesContentInOneDispatchedMutation()
    {
        var dispatcher = new RecordingDispatcher();
        var store = new MailListStore
        {
            CoreDispatcher = dispatcher,
        };
        await store.ResetAsync(
        [
            new MailItemViewModel(CreateMailCopy("old-thread")),
        ]);
        dispatcher.ExecutionCount = 0;

        var replacement = CreateMailCopy("new-thread");
        await store.ResetAsync(
            Enumerable.Range(0, 50)
                .Select(_ => new MailItemViewModel(CreateMailCopy("bulk-thread")))
                .Append(new MailItemViewModel(replacement)));

        dispatcher.ExecutionCount.Should().Be(1);
        store.Count.Should().Be(51);
        store.ContainsMailUniqueId(replacement.UniqueId).Should().BeTrue();
    }

    [Fact]
    public async Task ResetAsync_DropsPreviousIdentities()
    {
        var store = CreateStore();
        var outgoing = CreateMailCopy("outgoing-thread");
        await store.ResetAsync([new MailItemViewModel(outgoing)]);

        var incoming = CreateMailCopy("incoming-thread");
        await store.ResetAsync([new MailItemViewModel(incoming)]);

        store.ContainsMailUniqueId(outgoing.UniqueId).Should().BeFalse();
        store.ItemIds.Should().Equal(incoming.UniqueId);
    }

    [Fact]
    public async Task ResetAsync_WithNoItems_ClearsTheList()
    {
        var store = CreateStore();
        await store.ResetAsync([new MailItemViewModel(CreateMailCopy("thread-1"))]);

        await store.ResetAsync([]);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().BeEmpty();
        store.ItemIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetAsync_WhenRequestIsStale_LeavesPreviousContentInPlace()
    {
        var store = CreateStore();
        var retained = CreateMailCopy("retained-thread");
        await store.ResetAsync([new MailItemViewModel(retained)]);
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        store.Items.CollectionChanged += (_, args) => collectionChanges.Add(args.Action);

        await store.ResetAsync(
            [new MailItemViewModel(CreateMailCopy("stale-thread"))],
            shouldApply: static () => false);

        store.ItemIds.Should().Equal(retained.UniqueId);
        collectionChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetAsync_DeduplicatesIncomingIdentities()
    {
        var store = CreateStore();
        var duplicated = CreateMailCopy("thread-1");

        await store.ResetAsync(
        [
            new MailItemViewModel(duplicated),
            new MailItemViewModel(CloneMailCopy(duplicated)),
            null,
        ]);

        store.Count.Should().Be(1);
    }

    private static MailListStore CreateStore() => new()
    {
        CoreDispatcher = new ImmediateDispatcher(),
    };

    private static MailCopy CreateMailCopy(string threadId) => new()
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
        CreationDate = DateTime.UtcNow,
        Importance = MailImportance.Normal,
        ItemType = MailItemType.Mail,
        DraftId = string.Empty,
        FileId = Guid.NewGuid(),
    };

    private static MailCopy CloneMailCopy(MailCopy source) => new()
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
    };

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        public int ExecutionCount { get; set; }

        public Task ExecuteOnUIThread(Action action)
        {
            ExecutionCount++;
            action();
            return Task.CompletedTask;
        }
    }
}
