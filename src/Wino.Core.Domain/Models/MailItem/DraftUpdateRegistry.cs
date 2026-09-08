using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Models.MailItem;

/// <summary>Process-local draft ownership shared by persistence and background updates.</summary>
public sealed class DraftUpdateRegistry
{
    private readonly ConcurrentDictionary<(Guid Account, Guid Draft), Entry> _entries = new();
    public event Action<Guid, Guid> Mapped;

    public sealed class Entry
    {
        public SemaphoreSlim SaveLock { get; } = new(1, 1);
        public SemaphoreSlim PersistenceLock { get; } = new(1, 1);
        public Guid FileId { get; set; }
        public string MimeMessageId { get; set; }
        public Guid FolderId { get; set; }
        public volatile bool Protected;
        public string CurrentId;
        public long Version;
        public Guid Revision;
        public ConcurrentDictionary<string, byte> RemoteIds { get; } = new();
    }

    public Entry Get(Guid account, Guid draft) => _entries.GetOrAdd((account, draft), _ => new());

    public void Protect(Guid account, MailCopy mail, Guid revision = default)
    {
        var entry = Get(account, mail.UniqueId);
        lock (entry)
        {
            entry.CurrentId ??= mail.Id;
            entry.Revision = revision;
            entry.FileId = mail.FileId;
            entry.MimeMessageId = mail.MessageId;
            entry.FolderId = mail.FolderId;
            Remember(account, mail.UniqueId, mail.Id);
            Interlocked.Increment(ref entry.Version);
            entry.Protected = true;
        }
    }

    public void Remember(Guid account, Guid draft, string remoteId)
    {
        if (!string.IsNullOrEmpty(remoteId)) Get(account, draft).RemoteIds.TryAdd(remoteId, 0);
    }

    public void ConfirmIdentity(Guid account, Guid draft, string remoteId)
    {
        Remember(account, draft, remoteId);
        Get(account, draft).CurrentId = remoteId;
    }

    public bool IsStaleIdentity(Guid account, Guid draft, string id) =>
        _entries.TryGetValue((account, draft), out var entry) && entry.RemoteIds.ContainsKey(id ?? string.Empty) && entry.CurrentId != id;

    public bool IsStaleRemote(Guid account, string id) => _entries.Any(x =>
        x.Key.Account == account && x.Value.CurrentId != id && x.Value.RemoteIds.ContainsKey(id ?? string.Empty));

    public long FileVersion(Guid account, Guid file) =>
        _entries.Where(x => x.Key.Account == account && x.Value.FileId == file).Select(x => x.Value.Version).FirstOrDefault();

    public bool IsProtected(Guid account, Guid draft) =>
        _entries.TryGetValue((account, draft), out var entry) && entry.Protected;

    public bool IsFileProtected(Guid account, Guid file) =>
        _entries.Any(x => x.Key.Account == account && x.Value.FileId == file && x.Value.Protected);

    public bool IsRemoteProtected(Guid account, MailCopy mail) => _entries.Any(x =>
        x.Key.Account == account && x.Value.Protected &&
        (x.Key.Draft == mail.UniqueId || x.Value.RemoteIds.ContainsKey(mail.Id ?? string.Empty) ||
         (mail.IsDraft && (mail.FolderId == Guid.Empty || x.Value.FolderId == mail.FolderId) && !string.IsNullOrEmpty(mail.MessageId) &&
          x.Value.MimeMessageId == mail.MessageId)));

    public void NotifyMapped(Guid account, Guid draft) => Mapped?.Invoke(account, draft);
    public void Complete(Guid account, Guid draft, Guid revision)
    {
        var entry = Get(account, draft);
        lock (entry)
            if (entry.Revision == revision) entry.Protected = false;
    }

    public void Release(Guid account, Guid draft) => Get(account, draft).Protected = false;
    public void RemoveAccount(Guid account)
    {
        foreach (var key in _entries.Keys.Where(x => x.Account == account)) _entries.TryRemove(key, out _);
    }
}
