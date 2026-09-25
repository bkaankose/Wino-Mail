using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
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
    public async Task AddLiveRangeAsync_DeduplicatesLabelCopiesOfSameServerMessage()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var inboxFolderId = Guid.NewGuid();
        var inboxCopy = CreateMailCopy("gmail-thread");
        inboxCopy.AssignedAccount = account;
        inboxCopy.FolderId = inboxFolderId;
        inboxCopy.IsRead = true;
        await store.AddAsync(inboxCopy);

        var categoryCopy = CreateLabelCopy(inboxCopy, account, Guid.NewGuid());
        var unreadCopy = CreateLabelCopy(inboxCopy, account, Guid.NewGuid());
        unreadCopy.IsRead = false;

        await store.AddLiveRangeAsync(
            [categoryCopy, unreadCopy],
            EntityUpdateSource.Server,
            mail => mail.FolderId == inboxFolderId);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().ContainSingle()
            .Which.UniqueId.Should().Be(inboxCopy.UniqueId);
        store.Find(inboxCopy.UniqueId).IsRead.Should().BeFalse();
        using var projection = new MailListProjection(store.Items);
        projection.Rows.Should().ContainSingle()
            .Which.Kind.Should().Be(MailListRowKind.Single);
    }

    [Fact]
    public async Task AddLiveRangeAsync_DeduplicatesFlagLabelCopiesForEveryThreadMessage()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var inboxFolderId = Guid.NewGuid();
        var starredFolderId = Guid.NewGuid();
        var first = CreateMailCopy("gmail-thread");
        first.AssignedAccount = account;
        first.FolderId = inboxFolderId;
        var second = CreateMailCopy("gmail-thread");
        second.AssignedAccount = account;
        second.FolderId = inboxFolderId;
        await store.AddRangeAsync(
        [
            new MailItemViewModel(first),
            new MailItemViewModel(second),
        ], clearIdCache: true);

        var firstStarredCopy = CreateLabelCopy(first, account, starredFolderId);
        firstStarredCopy.IsFlagged = true;
        var secondStarredCopy = CreateLabelCopy(second, account, starredFolderId);
        secondStarredCopy.IsFlagged = true;

        await store.AddLiveRangeAsync(
            [firstStarredCopy, secondStarredCopy],
            EntityUpdateSource.Server,
            mail => mail.FolderId == inboxFolderId);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().HaveCount(2)
            .And.OnlyContain(item => item.IsFlagged);
        using var projection = new MailListProjection(store.Items);
        projection.Rows.Should().ContainSingle(row => row.IsThreadHead);
        projection.Threads.Should().ContainSingle()
            .Which.Count.Should().Be(2);
    }

    [Fact]
    public async Task ResetAndAppend_KeepSameGmailMessageIdentityAsLiveUpdates()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var first = CreateMailCopy("thread");
        first.AssignedAccount = account;
        var labelCopy = CreateLabelCopy(first, account, Guid.NewGuid());
        await store.ResetAsync(new[] { new MailItemViewModel(first), new MailItemViewModel(labelCopy) });
        store.Count.Should().Be(1);

        await store.AddRangeAsync(new[] { new MailItemViewModel(first) }, clearIdCache: false);
        store.Count.Should().Be(1);

        await store.AddLiveAsync(labelCopy, EntityUpdateSource.Server, static _ => true);
        store.Count.Should().Be(1);
    }

    [Fact]
    public async Task RemoveLiveRangeAsync_LabelRemovalKeepsMessageUntilLastCopyIsDeleted()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var unread = CreateMailCopy("conversation");
        unread.AssignedAccount = account;
        var sent = CreateLabelCopy(unread, account, Guid.NewGuid());
        var reply = CreateMailCopy("conversation");
        reply.AssignedAccount = account;
        await store.ResetAsync(new[] { new MailItemViewModel(unread), new MailItemViewModel(reply) });

        await store.RemoveLiveRangeAsync(new[] { unread }, new[] { sent }, static _ => true);

        store.Count.Should().Be(2);
        store.Find(sent.UniqueId).Should().NotBeNull();
        using var projection = new MailListProjection(store.Items);
        projection.Threads.Should().ContainSingle().Which.Count.Should().Be(2);

        await store.AddLiveAsync(unread, EntityUpdateSource.Server, static _ => true);
        store.Count.Should().Be(2);
        await store.RemoveLiveRangeAsync(new[] { sent, unread }, Array.Empty<MailCopy>(), static _ => true);
        store.Count.Should().Be(1);
    }

    [Fact]
    public async Task RemoveLiveRangeAsync_DoesNotReplaceInboxMailWithOffFolderGmailCopy()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var inboxFolderId = Guid.NewGuid();
        var inbox = CreateMailCopy("gmail-thread");
        inbox.AssignedAccount = account;
        inbox.FolderId = inboxFolderId;
        var allMail = CreateLabelCopy(inbox, account, Guid.NewGuid());
        await store.AddAsync(inbox);

        await store.RemoveLiveRangeAsync(
            [inbox], [allMail], mail => mail.FolderId == inboxFolderId);

        store.Count.Should().Be(0);
        store.Find(allMail.UniqueId).Should().BeNull();
    }

    [Fact]
    public async Task Projection_DoesNotMergeConversationsAcrossAccounts()
    {
        var store = CreateStore();
        var first = CreateMailCopy("same-thread");
        first.AssignedAccount = CreateGmailAccount();
        var second = CreateMailCopy("same-thread");
        second.AssignedAccount = CreateGmailAccount();
        await store.ResetAsync(new[] { new MailItemViewModel(first), new MailItemViewModel(second) });

        using var projection = new MailListProjection(store.Items);
        projection.Rows.Should().HaveCount(2).And.OnlyContain(row => row.Kind == MailListRowKind.Single);
    }

    [Fact]
    public async Task AddLiveAsync_KeepsDifferentMessagesInSameThread()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var first = CreateMailCopy("gmail-thread");
        first.AssignedAccount = account;
        var second = CreateMailCopy("gmail-thread");
        second.AssignedAccount = account;
        await store.AddAsync(first);

        await store.AddLiveAsync(second, EntityUpdateSource.Server, static _ => true);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().HaveCount(2);
        using var projection = new MailListProjection(store.Items);
        projection.Rows.Should().ContainSingle(row => row.IsThreadHead);
    }

    [Fact]
    public async Task AddLiveAsync_ReplacesOffFolderCopyWithActiveFolderCopy()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var inboxFolderId = Guid.NewGuid();
        var categoryCopy = CreateMailCopy("gmail-thread");
        categoryCopy.AssignedAccount = account;
        var inboxCopy = CreateLabelCopy(categoryCopy, account, inboxFolderId);
        await store.AddAsync(categoryCopy);

        await store.AddLiveAsync(
            inboxCopy,
            EntityUpdateSource.Server,
            mail => mail.FolderId == inboxFolderId);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().ContainSingle()
            .Which.UniqueId.Should().Be(inboxCopy.UniqueId);
    }

    [Fact]
    public async Task AddLiveAsync_DoesNotDeduplicateSameServerIdAcrossAccounts()
    {
        var store = CreateStore();
        var first = CreateMailCopy("gmail-thread");
        first.AssignedAccount = CreateGmailAccount();
        var second = CreateLabelCopy(first, CreateGmailAccount(), Guid.NewGuid());
        await store.AddAsync(first);

        await store.AddLiveAsync(second, EntityUpdateSource.Server, static _ => true);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().HaveCount(2);
    }

    [Fact]
    public async Task AddLiveAsync_DoesNotDeduplicateFolderScopedImapIds()
    {
        var store = CreateStore();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            ProviderType = MailProviderType.IMAP4,
        };
        var first = CreateMailCopy("imap-thread");
        first.AssignedAccount = account;
        var second = CreateLabelCopy(first, account, Guid.NewGuid());
        await store.AddAsync(first);

        await store.AddLiveAsync(second, EntityUpdateSource.Server, static _ => true);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().HaveCount(2);
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
    public async Task UpdateMailCopiesAsync_ClearingIntelligenceMetadata_UpdatesTheExistingRow()
    {
        var store = CreateStore();
        var mail = CreateMailCopy("thread-1");
        mail.IntelligenceMetadata = new MailIntelligenceMetadata(
            "outlook:test",
            ["important"],
            "normal",
            IncludeInBriefing: false);
        await store.AddAsync(mail);
        mail.IntelligenceMetadata = null;

        await store.UpdateMailCopiesAsync(
            [mail],
            EntityUpdateSource.Server,
            MailCopyChangeFlags.IntelligenceMetadata);

        store.Find(mail.UniqueId).IntelligenceTiles.Should().BeEmpty();
        store.Find(mail.UniqueId).RowTiles.Should().BeEmpty();
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

    [Fact]
    public async Task Indexes_FollowAddRemoveResetAndClear()
    {
        var store = CreateStore();
        var first = CreateMailCopy("thread-a");
        var second = CreateMailCopy("thread-a");
        var third = CreateMailCopy("thread-b");
        var threadA = MailConversationIdentity.ThreadKey(first);
        var threadB = MailConversationIdentity.ThreadKey(third);

        await store.AddRangeAsync([new MailItemViewModel(first), new MailItemViewModel(second), new MailItemViewModel(third)], clearIdCache: true);
        store.ContainsThreadKey(threadA).Should().BeTrue();
        store.ContainsThreadKey(threadB).Should().BeTrue();
        store.ItemIds.Should().BeEquivalentTo([first.UniqueId, second.UniqueId, third.UniqueId]);

        await store.RemoveAsync(first);
        store.ContainsThreadKey(threadA).Should().BeTrue("one leaf of the thread is still listed");
        store.ContainsMailUniqueId(first.UniqueId).Should().BeFalse();
        store.Find(second.UniqueId).Should().NotBeNull();

        await store.RemoveAsync(second);
        store.ContainsThreadKey(threadA).Should().BeFalse();

        await store.ResetAsync([new MailItemViewModel(first)]);
        store.ContainsThreadKey(threadA).Should().BeTrue();
        store.ContainsThreadKey(threadB).Should().BeFalse();
        store.ItemIds.Should().BeEquivalentTo([first.UniqueId]);

        await store.ClearAsync();
        store.ContainsThreadKey(threadA).Should().BeFalse();
        store.ItemIds.Should().BeEmpty();
        store.Find(first.UniqueId).Should().BeNull();
    }

    [Fact]
    public async Task UpdatingThreadId_MovesTheThreadKeyIndex()
    {
        var store = CreateStore();
        var mail = CreateMailCopy("before");
        var previousKey = MailConversationIdentity.ThreadKey(mail);
        await store.AddAsync(mail);
        var updated = CloneMailCopy(mail);
        updated.ThreadId = "after";

        await store.UpdateMailCopy(updated, EntityUpdateSource.Server, MailCopyChangeFlags.ThreadId);

        store.ContainsThreadKey(previousKey).Should().BeFalse();
        store.ContainsThreadKey(MailConversationIdentity.ThreadKey(updated)).Should().BeTrue();
        store.Find(mail.UniqueId).ThreadKey.Should().Be(MailConversationIdentity.ThreadKey(updated));
    }

    [Fact]
    public async Task SameInstanceUpdate_RefreshesTheRow_WithoutRebuildingTheProjection()
    {
        var store = CreateStore();
        var mail = CreateMailCopy("thread-1");
        await store.AddRangeAsync([new MailItemViewModel(mail), new MailItemViewModel(CreateMailCopy("thread-2"))], clearIdCache: true);
        using var projection = new MailListProjection(store.Items);
        var rebuilds = 0;
        projection.ProjectionChanged += (_, _) => rebuilds++;
        var item = store.Find(mail.UniqueId);
        var notified = new List<string>();
        item.PropertyChanged += (_, args) => notified.Add(args.PropertyName);

        // The same instance was mutated in place, as client-side operations do.
        mail.IsRead = true;
        await store.UpdateMailCopy(mail, EntityUpdateSource.ClientUpdated);

        rebuilds.Should().Be(0);
        notified.Should().Contain(nameof(MailItemViewModel.IsRead));
        notified.Should().NotContain(nameof(MailItemViewModel.DateSortKey));
        item.IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveLiveRangeAsync_IgnoresMailThatIsNotListed()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var listed = CreateMailCopy("thread-1");
        listed.AssignedAccount = account;
        await store.AddAsync(listed);
        var unlisted = CreateMailCopy("thread-2");
        unlisted.AssignedAccount = account;
        var unlistedSibling = CreateLabelCopy(unlisted, account, Guid.NewGuid());

        await store.RemoveLiveRangeAsync([unlisted], [unlistedSibling], null);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().ContainSingle()
            .Which.UniqueId.Should().Be(listed.UniqueId);
    }

    [Fact]
    public async Task AddRangeAsync_WithPreferredSeed_KeepsTheListedCopyInstance()
    {
        var store = CreateStore();
        var account = CreateGmailAccount();
        var inboxFolderId = Guid.NewGuid();
        var starredFolderId = Guid.NewGuid();
        var inboxCopy = CreateMailCopy("thread-1");
        inboxCopy.AssignedAccount = account;
        inboxCopy.FolderId = inboxFolderId;
        inboxCopy.AssignedFolder = new MailItemFolder { Id = inboxFolderId, MailAccountId = account.Id, SpecialFolderType = SpecialFolderType.Inbox };
        var starredCopy = CreateLabelCopy(inboxCopy, account, starredFolderId);
        starredCopy.AssignedFolder = new MailItemFolder { Id = starredFolderId, MailAccountId = account.Id, SpecialFolderType = SpecialFolderType.Starred };

        // The starred view lists the starred copy; a later page also carries the inbox copy.
        await store.AddRangeAsync([new MailItemViewModel(starredCopy)], clearIdCache: true);
        var listedInstance = store.Find(starredCopy.UniqueId);

        await store.AddRangeAsync(
            [new MailItemViewModel(inboxCopy)],
            clearIdCache: false,
            isPreferred: mail => mail.FolderId == starredFolderId);

        ((IEnumerable<MailItemViewModel>)store.Items).Should().ContainSingle()
            .Which.Should().BeSameAs(listedInstance);
    }

    [Fact]
    public async Task ItemIds_SnapshotIsStableWhileTheListMutates()
    {
        var store = CreateStore();
        await store.AddRangeAsync(Enumerable.Range(0, 50).Select(_ => new MailItemViewModel(CreateMailCopy("t"))), clearIdCache: true);

        var snapshot = store.ItemIds;
        await store.ClearAsync();

        snapshot.Should().HaveCount(50);
        store.ItemIds.Should().BeEmpty();
    }

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

    private static MailCopy CreateLabelCopy(MailCopy source, MailAccount account, Guid folderId)
    {
        var copy = CloneMailCopy(source);
        copy.UniqueId = Guid.NewGuid();
        copy.FolderId = folderId;
        copy.AssignedAccount = account;
        return copy;
    }

    private static MailAccount CreateGmailAccount() => new()
    {
        Id = Guid.NewGuid(),
        ProviderType = MailProviderType.Gmail,
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
