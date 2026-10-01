using System;
using System.Threading;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>Retains the remote identity of one local draft across observer lifetimes.</summary>
public sealed class DraftMappingLifecycle(Guid accountId, Guid uniqueId)
{
    private readonly object _gate = new();
    private DraftUpdateIdentity _remoteIdentity;
    private event Action<DraftMappingLifecycle> Changed;

    public bool HasRemoteMapping
    {
        get { lock (_gate) return _remoteIdentity != null; }
    }

    /// <summary>Registers before delivering current state. Notifications always expose the latest identity.</summary>
    public IDisposable Observe(Action<DraftMappingLifecycle> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate) Changed += observer;
        var subscription = new Subscription(this, observer);
        try
        {
            observer(this);
            return subscription;
        }
        catch
        {
            subscription.Dispose();
            throw;
        }
    }

    /// <summary>Restores state from an already persisted remote draft without replacing a newer mapping.</summary>
    public void Initialize(MailCopy draft)
    {
        ValidateDraft(draft);
        if (draft.IsLocalDraft) return;

        Action<DraftMappingLifecycle> observers;
        lock (_gate)
        {
            if (_remoteIdentity != null) return;
            _remoteIdentity = DraftUpdateIdentity.From(draft);
            observers = Changed;
        }
        observers?.Invoke(this);
    }

    /// <summary>Called after the mapping or replacement identity has committed to storage.</summary>
    public void Confirm(DraftUpdateIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(identity.MessageId);
        Action<DraftMappingLifecycle> observers;
        lock (_gate)
        {
            if (_remoteIdentity == identity) return;
            _remoteIdentity = identity;
            observers = Changed;
        }

        observers?.Invoke(this);
    }

    /// <summary>Applies only mapping fields on the caller's thread, preserving all edited content.</summary>
    public bool ApplyTo(MailCopy draft)
    {
        ValidateDraft(draft);
        lock (_gate)
        {
            if (_remoteIdentity == null || DraftUpdateIdentity.From(draft) == _remoteIdentity) return false;

            var wasLocal = draft.IsLocalDraft;
            _remoteIdentity.Apply(draft);
            if (wasLocal)
            {
                draft.DraftSyncState = DraftSyncState.Synced;
                draft.DraftSyncAttemptCount = 0;
                draft.LastDraftSyncError = null;
            }
            return true;
        }
    }

    private void ValidateDraft(MailCopy draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.IsDraft || draft.UniqueId != uniqueId || draft.AssignedAccount?.Id != accountId)
            throw new ArgumentException("The draft does not belong to this mapping lifecycle.", nameof(draft));
    }

    private sealed class Subscription(DraftMappingLifecycle owner, Action<DraftMappingLifecycle> observer) : IDisposable
    {
        private DraftMappingLifecycle _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current != null)
                lock (current._gate) current.Changed -= observer;
        }
    }
}
