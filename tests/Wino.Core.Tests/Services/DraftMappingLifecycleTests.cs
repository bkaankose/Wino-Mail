using FluentAssertions;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Xunit;

namespace Wino.Core.Tests.Services;

public class DraftMappingLifecycleTests
{
    [Fact]
    public void Late_observer_gets_mapping_and_preserves_local_edits()
    {
        var draft = LocalDraft();
        var mapping = new DraftMappingLifecycle(draft.AssignedAccount.Id, draft.UniqueId);
        mapping.Confirm(new("remote", "draft", "thread", 42, 5));
        using var subscription = mapping.Observe(state => state.ApplyTo(draft));

        mapping.HasRemoteMapping.Should().BeTrue();
        draft.IsLocalDraft.Should().BeFalse();
        draft.Id.Should().Be("remote");
        draft.ImapUid.Should().Be(42);
        draft.Subject.Should().Be("local edits");
        draft.DraftSyncState.Should().Be(DraftSyncState.Synced);
    }

    [Fact]
    public void Queued_notification_reads_latest_replacement_identity()
    {
        var draft = LocalDraft();
        var mapping = new DraftMappingLifecycle(draft.AssignedAccount.Id, draft.UniqueId);
        var queued = new List<Action>();
        using var subscription = mapping.Observe(state => queued.Add(() => state.ApplyTo(draft)));
        mapping.Confirm(new("first", "draft", "thread", 1, 5));
        mapping.Confirm(new("second", "draft", "thread", 2, 5));
        foreach (var apply in queued.AsEnumerable().Reverse()) apply();

        draft.Id.Should().Be("second");
        draft.ImapUid.Should().Be(2);
    }

    [Fact]
    public void Disposing_observer_keeps_mapping_for_next_observer()
    {
        var draft = LocalDraft();
        var mapping = new DraftMappingLifecycle(draft.AssignedAccount.Id, draft.UniqueId);
        var calls = 0;
        var subscription = mapping.Observe(_ => calls++);
        subscription.Dispose();
        subscription.Dispose();
        mapping.Confirm(new("remote", "draft", "thread"));
        calls.Should().Be(1);
        using var reopened = mapping.Observe(state => state.ApplyTo(draft));
        draft.Id.Should().Be("remote");
    }

    [Fact]
    public void Persisted_remote_draft_restores_mapping_without_overwriting_newer_identity()
    {
        var draft = LocalDraft();
        draft.Id = "persisted";
        draft.DraftId = "remote-draft";
        var mapping = new DraftMappingLifecycle(draft.AssignedAccount.Id, draft.UniqueId);
        mapping.Initialize(draft);
        mapping.HasRemoteMapping.Should().BeTrue();
        mapping.Confirm(new("newer", "remote-draft", "thread"));
        mapping.Initialize(draft);
        mapping.ApplyTo(draft);
        draft.Id.Should().Be("newer");
    }

    [Fact]
    public void Mapping_stays_confirmed_during_later_uploads()
    {
        var draft = LocalDraft();
        var mapping = new DraftMappingLifecycle(draft.AssignedAccount.Id, draft.UniqueId);
        mapping.Confirm(new("remote", "draft", "thread"));
        mapping.ApplyTo(draft);
        draft.DraftSyncState = DraftSyncState.PendingSync;
        mapping.Confirm(new("replacement", "draft", "thread"));
        mapping.ApplyTo(draft);
        mapping.HasRemoteMapping.Should().BeTrue();
        draft.DraftSyncState.Should().Be(DraftSyncState.PendingSync);
    }

    [Fact]
    public void Accounts_have_independent_mapping_for_same_local_id()
    {
        var draft = LocalDraft();
        var registry = new DraftUpdateRegistry();
        registry.ConfirmMapping(draft.AssignedAccount.Id, draft.UniqueId, new("remote", "draft", "thread"));
        registry.Get(Guid.NewGuid(), draft.UniqueId).Mapping.HasRemoteMapping.Should().BeFalse();
        var wrongAccount = LocalDraft();
        wrongAccount.UniqueId = draft.UniqueId;
        var apply = () => registry.Get(draft.AssignedAccount.Id, draft.UniqueId).Mapping.ApplyTo(wrongAccount);
        apply.Should().Throw<ArgumentException>();
    }

    private static MailCopy LocalDraft() => new()
    {
        UniqueId = Guid.NewGuid(), AssignedAccount = new MailAccount { Id = Guid.NewGuid() },
        IsDraft = true, Id = "local", DraftId = "localDraft_initial", Subject = "local edits",
        DraftSyncState = DraftSyncState.PendingSync
    };
}
