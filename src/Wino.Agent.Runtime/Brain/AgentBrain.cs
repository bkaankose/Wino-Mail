using System.Text.Json;
using Wino.Agent.Runtime.Plan;
using Wino.Agent.Runtime.Ports;

namespace Wino.Agent.Runtime.Brain;

/// <summary>The local brain as the app and the memory screen see it. Writes are saved through <see cref="IAgentBrainStorage"/>.</summary>
public interface IAgentBrain
{
    /// <summary>Live entries (no tombstones), newest change first.</summary>
    IReadOnlyList<BrainEntry> Entries { get; }

    event EventHandler? Changed;

    Task LoadAsync(CancellationToken ct);

    Task FlushAsync(CancellationToken ct);

    /// <summary>Writes an entry (replace by kind + subject + value). Returns the stored entry.</summary>
    Task<BrainEntry> RememberAsync(BrainEntryKind kind, BrainSubject subject, string value, BrainSource source, string? saidAs, CancellationToken ct);

    Task ForgetAsync(string entryId, CancellationToken ct);

    Task ForgetAllAsync(CancellationToken ct);

    /// <summary>The plain document (for the developer export and for sync encryption).</summary>
    BrainDocument Export();

    /// <summary>Merges a document into this brain per entry (newer <c>changedAt</c> wins, tombstones win over older live entries).</summary>
    Task ImportAsync(BrainDocument document, CancellationToken ct);
}

/// <summary>The in-memory brain with its queries by kind, subject and value, and the store behind it.</summary>
public sealed class AgentBrain : IAgentBrain
{
    public const int MaxLiveEntries = 500;
    public const int MaxPicks = 200;
    public static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(90);
    public static readonly TimeSpan DeclineRetention = TimeSpan.FromDays(30);

    private readonly BrainStore _store;
    private readonly object _gate = new();
    private BrainDocument _document = new();
    private bool _loaded;

    public AgentBrain(IAgentBrainStorage storage, TimeProvider? time = null)
    {
        _store = new BrainStore(storage);
        Time = time ?? TimeProvider.System;
    }

    public TimeProvider Time { get; }

    public event EventHandler? Changed;

    public IReadOnlyList<BrainEntry> Entries
    {
        get { lock (_gate) return _document.Entries.Where(e => e.IsLive).OrderByDescending(e => e.ChangedAt).ToList(); }
    }

    public string DeviceId
    {
        get { lock (_gate) return _document.DeviceId; }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var loaded = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _document = loaded ?? new BrainDocument { DeviceId = Guid.NewGuid().ToString("N") };
            if (string.IsNullOrEmpty(_document.DeviceId)) _document.DeviceId = Guid.NewGuid().ToString("N");
            _loaded = true;
            Prune();
        }
    }

    public Task FlushAsync(CancellationToken ct) => _store.FlushAsync(ct);

    // ---- queries ------------------------------------------------------------------------------------------------

    public IReadOnlyList<BrainEntry> Find(BrainEntryKind kind, string? value = null)
    {
        lock (_gate)
            return _document.Entries.Where(e => e.IsLive && e.Kind == kind && (value is null || string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public IReadOnlyList<BrainEntry> FindBySubject(string subjectKey)
    {
        lock (_gate) return _document.Entries.Where(e => e.IsLive && e.Subject.Key == subjectKey).ToList();
    }

    /// <summary>The pick the user made for these words and this kind, if any (and not declined).</summary>
    public BrainEntry? FindPick(string foldedWords, ItemKind? kind)
    {
        if (foldedWords.Length == 0) return null;
        lock (_gate)
            return _document.Entries.FirstOrDefault(e => e.IsLive && e.Kind == BrainEntryKind.Pick && e.Subject.Text == foldedWords
                && (kind is null || e.Subject.ItemKind == kind.ToString()));
    }

    public bool IsDeclined(string signature)
    {
        lock (_gate)
            return _document.Entries.Any(e => e.IsLive && e.Kind == BrainEntryKind.Declined && e.Value == signature && e.CreatedAt > Time.GetUtcNow() - DeclineRetention);
    }

    public IReadOnlyList<BrainEntry> PreferencesFor(string subjectKey) => FindBySubject(subjectKey).Where(e => e.Kind == BrainEntryKind.Preference).ToList();

    // ---- writes -------------------------------------------------------------------------------------------------

    public async Task<BrainEntry> RememberAsync(BrainEntryKind kind, BrainSubject subject, string value, BrainSource source, string? saidAs, CancellationToken ct)
    {
        BrainEntry entry;
        lock (_gate)
        {
            var now = Time.GetUtcNow();
            var subjectKey = subject.Key;
            entry = _document.Entries.FirstOrDefault(e => e.Kind == kind && e.Subject.Key == subjectKey && string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase))
                ?? new BrainEntry { Id = Ulid.NewUlid(now), Kind = kind, CreatedAt = now };
            if (entry.Id.Length == 0) entry.Id = Ulid.NewUlid(now);
            if (!_document.Entries.Contains(entry)) _document.Entries.Add(entry);
            entry.Subject = subject.Clone();
            entry.Value = value;
            entry.Source = source;
            entry.ChangedAt = now;
            entry.DeletedAt = null;
            entry.DeviceId = _document.DeviceId;
            if (!string.IsNullOrWhiteSpace(saidAs) && !entry.SaidAs.Contains(saidAs, StringComparer.OrdinalIgnoreCase)) entry.SaidAs.Add(saidAs);
            if (entry.SaidAs.Count > 8) entry.SaidAs.RemoveAt(0);
            Prune();
        }

        await SaveAsync(ct).ConfigureAwait(false);
        return entry;
    }

    /// <summary>Tombstones every live entry of the kind + value whose subject differs (a correction: "no, my father is Mehmet").</summary>
    public async Task ReplaceAsync(BrainEntryKind kind, string value, BrainSubject keep, CancellationToken ct)
    {
        lock (_gate)
        {
            var keepKey = keep.Key;
            foreach (var e in _document.Entries.Where(e => e.IsLive && e.Kind == kind && string.Equals(e.Value, value, StringComparison.OrdinalIgnoreCase) && e.Subject.Key != keepKey))
                Tombstone(e);
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task ForgetAsync(string entryId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_document.Entries.FirstOrDefault(e => e.Id == entryId) is { } e) Tombstone(e);
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task ForgetWhereAsync(Func<BrainEntry, bool> predicate, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var e in _document.Entries.Where(e => e.IsLive && predicate(e)).ToList()) Tombstone(e);
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task ForgetAllAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var e in _document.Entries.Where(e => e.IsLive)) Tombstone(e);
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Changes a group's members (adds or removes one); an empty group is tombstoned.</summary>
    public async Task UpdateGroupAsync(string concept, IReadOnlyList<string> add, IReadOnlyList<string> remove, string? saidAs, CancellationToken ct)
    {
        var existing = Find(BrainEntryKind.Group).FirstOrDefault(g => string.Equals(g.Subject.Text, concept, StringComparison.OrdinalIgnoreCase));
        var members = (existing?.Members ?? []).ToList();
        foreach (var a in add) if (!members.Contains(a, StringComparer.OrdinalIgnoreCase)) members.Add(a);
        members.RemoveAll(m => remove.Contains(m, StringComparer.OrdinalIgnoreCase));
        if (existing is not null) await ForgetAsync(existing.Id, ct).ConfigureAwait(false);
        if (members.Count > 0) await RememberAsync(BrainEntryKind.Group, BrainSubject.Concept(concept), string.Join(';', members), BrainSource.Stated, saidAs, ct).ConfigureAwait(false);
    }

    public BrainDocument Export()
    {
        lock (_gate)
            return new BrainDocument { V = _document.V, DeviceId = _document.DeviceId, Entries = _document.Entries.Select(e => e.Clone()).ToList() };
    }

    public async Task ImportAsync(BrainDocument document, CancellationToken ct)
    {
        lock (_gate)
        {
            _document = BrainMerge.Merge(_document, document, Time.GetUtcNow());
            Prune();
        }

        await SaveAsync(ct).ConfigureAwait(false);
    }

    public string ExportJson() => JsonSerializer.Serialize(Export(), BrainJsonContext.Default.BrainDocument);

    private void Tombstone(BrainEntry e)
    {
        e.DeletedAt = Time.GetUtcNow();
        e.ChangedAt = e.DeletedAt.Value;
        e.DeviceId = _document.DeviceId;
    }

    /// <summary>Drops old tombstones and expired declines, evicts the oldest picks over the cap and the oldest entries over the live cap.</summary>
    private void Prune()
    {
        var now = Time.GetUtcNow();
        _document.Entries.RemoveAll(e => e.DeletedAt is { } d && now - d > TombstoneRetention);
        foreach (var e in _document.Entries.Where(e => e.IsLive && e.Kind == BrainEntryKind.Declined && now - e.CreatedAt > DeclineRetention)) Tombstone(e);
        var picks = _document.Entries.Where(e => e.IsLive && e.Kind == BrainEntryKind.Pick).OrderBy(e => e.ChangedAt).ToList();
        foreach (var e in picks.Take(Math.Max(0, picks.Count - MaxPicks))) Tombstone(e);
        var live = _document.Entries.Where(e => e.IsLive).OrderBy(e => e.ChangedAt).ToList();
        foreach (var e in live.Take(Math.Max(0, live.Count - MaxLiveEntries))) Tombstone(e);
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        BrainDocument snapshot;
        lock (_gate) snapshot = Export();
        if (_loaded) await _store.SaveAsync(snapshot, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Load and save through <see cref="IAgentBrainStorage"/>; saves are coalesced (debounced) so a burst of picks writes once.</summary>
public sealed class BrainStore(IAgentBrainStorage storage)
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private BrainDocument? _pending;
    private Task? _flush;

    public async Task<BrainDocument?> LoadAsync(CancellationToken ct)
    {
        var bytes = await storage.ReadAsync(ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize(bytes, BrainJsonContext.Default.BrainDocument);
        }
        catch (JsonException)
        {
            // An unreadable file is not worth losing the session over: start empty, the next save overwrites it.
            return null;
        }
    }

    public Task SaveAsync(BrainDocument document, CancellationToken ct)
    {
        lock (_lock)
        {
            _pending = document;
            _flush ??= FlushLaterAsync();
        }

        return Task.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        Task? flush;
        lock (_lock) flush = _flush;
        if (flush is not null) await flush.ConfigureAwait(false);
        await WritePendingAsync(ct).ConfigureAwait(false);
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(Debounce).ConfigureAwait(false);
        lock (_lock) _flush = null;
        await WritePendingAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WritePendingAsync(CancellationToken ct)
    {
        BrainDocument? doc;
        lock (_lock) { doc = _pending; _pending = null; }
        if (doc is null) return;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await storage.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(doc, BrainJsonContext.Default.BrainDocument), ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }
}

/// <summary>Per-entry merge for sync (plan 4.10): same id, newer <c>changedAt</c> wins; a tombstone newer than the live entry wins; unknown ids are added.</summary>
public static class BrainMerge
{
    public static BrainDocument Merge(BrainDocument local, BrainDocument remote, DateTimeOffset now)
    {
        var result = new BrainDocument { V = Math.Max(local.V, remote.V), DeviceId = local.DeviceId, Entries = local.Entries.Select(e => e.Clone()).ToList() };
        var byId = result.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var r in remote.Entries)
        {
            if (!byId.TryGetValue(r.Id, out var mine))
            {
                // A missing entry older than the tombstone retention counts as deleted here (the tombstone was dropped).
                if (r.IsLive && now - r.ChangedAt > AgentBrain.TombstoneRetention) continue;
                var clone = r.Clone();
                result.Entries.Add(clone);
                byId[r.Id] = clone;
                continue;
            }

            if (r.ChangedAt > mine.ChangedAt)
            {
                var index = result.Entries.IndexOf(mine);
                result.Entries[index] = r.Clone();
                byId[r.Id] = result.Entries[index];
            }
        }

        return result;
    }
}
