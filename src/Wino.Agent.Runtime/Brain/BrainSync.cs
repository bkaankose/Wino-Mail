using System.Text.Json;
using System.Text.Json.Serialization;
using Wino.Agent.Runtime.Ports;

namespace Wino.Agent.Runtime.Brain;

/// <summary>
/// "Sync my brain" (plan section 4.10): the brain encrypted end to end and kept as one blob per user on the server.
/// The password is turned into a key (Argon2id, fresh salt) once, and only the key is kept, in
/// <see cref="IAgentBrainStorage.WriteSecret"/> under <see cref="KeySecretName"/>; the salt, the parameters and the
/// last known server revision are the sync state next to it. Local changes upload the whole blob after a debounce
/// with <c>If-Match</c>; a conflict downloads, merges per entry (<see cref="BrainMerge"/>: newer <c>changedAt</c>
/// wins, tombstones win over older live entries) and uploads again, at most three rounds. App start and foreground
/// only ask for the revision and download when it differs. A download the key cannot open stops syncing with
/// <see cref="BrainSyncProblem.WrongPassword"/> until the user replaces the server copy or enters the password.
/// </summary>
public sealed class BrainSync : IAsyncDisposable
{
    public const string KeySecretName = "brain-sync-key";
    public const string StateSecretName = "brain-sync-state";
    public const int MaxConflictRounds = 3;
    public static readonly TimeSpan UploadDebounce = TimeSpan.FromSeconds(5);

    private readonly AgentBrain _brain;
    private readonly IAgentBrainStorage _storage;
    private readonly IBrainSyncTransport _transport;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private SyncState? _state;
    private byte[]? _key;
    private CancellationTokenSource? _debounce;
    private bool _disposed;

    public BrainSync(AgentBrain brain, IAgentBrainStorage storage, IBrainSyncTransport transport, TimeProvider? time = null)
    {
        _brain = brain;
        _storage = storage;
        _transport = transport;
        _time = time ?? TimeProvider.System;
        Load();
        _brain.Changed += BrainChanged;
    }

    /// <summary>Raised after every status change, on whatever thread finished the work.</summary>
    public event EventHandler? StatusChanged;

    public bool IsEnabled
    {
        get { lock (_stateLock) return _state is not null && _key is not null; }
    }

    public BrainSyncStatus Status
    {
        get
        {
            lock (_stateLock)
                return new BrainSyncStatus(_state is not null && _key is not null, _state?.LastKnownRevision, _state?.LastSyncedUtc, _state?.Dirty ?? false, _state?.Problem ?? BrainSyncProblem.None, _state?.PendingServerBlob is not null);
        }
    }

    /// <summary>Turns sync on with <paramref name="password"/>: derives the key, then uploads or merges with the server copy.</summary>
    public async Task EnableAsync(string password, CancellationToken ct)
    {
        var parameters = BrainCrypto.CreateParameters();
        var key = await BrainCrypto.DeriveKeyAsync(password, parameters, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                _key = key;
                _state = new SyncState { Parameters = parameters, Dirty = true };
                Save();
            }

            await ReconcileAsync(password, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Answers <see cref="BrainSyncProblem.WrongPassword"/> with the password of the server copy: opens it, merges and re-encrypts with this device's key.</summary>
    public async Task EnterPasswordAsync(string password, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsEnabled) return;
            await ReconcileAsync(password, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Answers <see cref="BrainSyncProblem.WrongPassword"/> by overwriting the server copy with this device's brain.</summary>
    public async Task ReplaceServerCopyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsEnabled) return;
            SetProblem(BrainSyncProblem.None);
            await UploadAsync(null, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Re-derives the key with a fresh salt, re-encrypts once and uploads with <c>If-Match</c>.</summary>
    public async Task ChangePasswordAsync(string newPassword, CancellationToken ct)
    {
        var parameters = BrainCrypto.CreateParameters();
        var key = await BrainCrypto.DeriveKeyAsync(newPassword, parameters, ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsEnabled) return;
            lock (_stateLock)
            {
                _key = key;
                _state!.Parameters = parameters;
                _state.Dirty = true;
                _state.Problem = BrainSyncProblem.None;
                Save();
            }

            await UploadWithRetryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Sync off: forgets the key and the state; optionally deletes the server copy.</summary>
    public async Task DisableAsync(bool deleteServerCopy, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CancelDebounce();
            if (deleteServerCopy)
            {
                try { await _transport.DeleteAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { SetProblem(BrainSyncProblem.Offline); throw; }
            }

            lock (_stateLock)
            {
                _key = null;
                _state = null;
                _storage.DeleteSecret(KeySecretName);
                _storage.DeleteSecret(StateSecretName);
            }

            Notify();
        }
        finally { _gate.Release(); }
    }

    /// <summary>App start or foreground: the revision only; downloads and merges when it differs; uploads when dirty.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        if (!IsEnabled) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsEnabled || Status.Problem == BrainSyncProblem.WrongPassword) return;
            string? revision;
            try { revision = await _transport.GetRevisionAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { SetProblem(BrainSyncProblem.Offline); return; }

            var known = Status.LastKnownRevision;
            if (revision is not null && revision != known)
            {
                if (!await DownloadAndMergeAsync(null, ct).ConfigureAwait(false)) return;
            }
            else if (revision is null && known is not null)
            {
                // The server copy is gone (another device turned sync off and deleted it): ours is the only one, re-upload it.
                lock (_stateLock) { _state!.LastKnownRevision = null; _state.Dirty = true; Save(); }
            }

            if (Status.Dirty) await UploadWithRetryAsync(ct).ConfigureAwait(false);
            else SetProblem(BrainSyncProblem.None);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Waits for a pending debounced upload (tests and app shutdown).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        CancelDebounce();
        if (!IsEnabled || !Status.Dirty) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await UploadWithRetryAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _brain.Changed -= BrainChanged;
        CancelDebounce();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ---- internals ----------------------------------------------------------------------------------------------

    private void BrainChanged(object? sender, EventArgs e)
    {
        if (_disposed || !IsEnabled) return;
        lock (_stateLock) { _state!.Dirty = true; Save(); }
        Notify();
        ScheduleUpload();
    }

    private void ScheduleUpload()
    {
        CancellationTokenSource cts;
        lock (_stateLock)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = cts = new CancellationTokenSource();
        }

        _ = DebouncedUploadAsync(cts);
    }

    private async Task DebouncedUploadAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(UploadDebounce, _time, cts.Token).ConfigureAwait(false);
            await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (IsEnabled && Status.Dirty && Status.Problem != BrainSyncProblem.WrongPassword)
                    await UploadWithRetryAsync(cts.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { SetProblem(BrainSyncProblem.Offline); }
    }

    private void CancelDebounce()
    {
        lock (_stateLock)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    /// <summary>Enable / enter password: merges with whatever the server holds, using <paramref name="password"/> when the server copy has another salt.</summary>
    private async Task ReconcileAsync(string password, CancellationToken ct)
    {
        string? revision;
        try { revision = await _transport.GetRevisionAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { SetProblem(BrainSyncProblem.Offline); return; }

        if (revision is null)
        {
            await UploadAsync("0", ct).ConfigureAwait(false);
            return;
        }

        if (!await DownloadAndMergeAsync(password, ct).ConfigureAwait(false)) return;
        await UploadWithRetryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Downloads, decrypts (with the key, or with <paramref name="password"/> re-derived for a foreign salt) and merges. False when syncing must stop.</summary>
    private async Task<bool> DownloadAndMergeAsync(string? password, CancellationToken ct)
    {
        BrainBlob? blob;
        try { blob = await _transport.DownloadAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { SetProblem(BrainSyncProblem.Offline); return false; }
        if (blob is null)
        {
            lock (_stateLock) { _state!.LastKnownRevision = null; _state.Dirty = true; Save(); }
            return true;
        }

        BrainDocument remote;
        try
        {
            var header = BrainCrypto.ReadHeader(blob.Bytes);
            byte[]? key;
            lock (_stateLock) key = _state!.Parameters.SameAs(header.Parameters) ? _key : null;
            if (key is null)
            {
                if (password is null)
                {
                    SetProblem(BrainSyncProblem.WrongPassword);
                    return false;
                }

                key = await BrainCrypto.DeriveKeyAsync(password, header.Parameters, ct).ConfigureAwait(false);
            }

            remote = BrainCrypto.DecryptBlob(blob.Bytes, key);
        }
        catch (BrainKeyMismatchException)
        {
            SetProblem(BrainSyncProblem.WrongPassword);
            return false;
        }
        catch (BrainBlobInvalidException)
        {
            SetProblem(BrainSyncProblem.InvalidServerCopy);
            return false;
        }

        var local = _brain.Export();
        var merged = BrainMerge.Merge(local, remote, _time.GetUtcNow());
        var localChanged = !DocumentsEqual(local, merged);
        var remoteBehind = !DocumentsEqual(remote, merged);

        if (localChanged)
        {
            // ImportAsync saves and raises Changed; the upload below carries the merged document, so the flag is right either way.
            await _brain.ImportAsync(remote, ct).ConfigureAwait(false);
        }

        lock (_stateLock)
        {
            _state!.LastKnownRevision = blob.Revision;
            _state.Dirty = remoteBehind;
            _state.Problem = BrainSyncProblem.None;
            Save();
        }

        Notify();
        return true;
    }

    private async Task UploadWithRetryAsync(CancellationToken ct)
    {
        for (var round = 0; round < MaxConflictRounds; round++)
        {
            string? expected;
            lock (_stateLock) expected = _state?.LastKnownRevision ?? "0";
            try
            {
                await UploadAsync(expected, ct).ConfigureAwait(false);
                return;
            }
            catch (BrainSyncConflictException)
            {
                if (!await DownloadAndMergeAsync(null, ct).ConfigureAwait(false)) return;
                if (!Status.Dirty) return;
            }
        }
    }

    /// <summary>One upload of the current brain; <paramref name="ifMatch"/> null overwrites unconditionally.</summary>
    private async Task UploadAsync(string? ifMatch, CancellationToken ct)
    {
        byte[] blob;
        try
        {
            byte[] key;
            BrainKeyParameters parameters;
            lock (_stateLock) { key = _key!; parameters = _state!.Parameters; }
            blob = new BrainCrypto(parameters).Encrypt(_brain.Export(), key);
        }
        catch (BrainBlobTooLargeException)
        {
            SetProblem(BrainSyncProblem.TooLarge);
            return;
        }

        string revision;
        try { revision = await _transport.UploadAsync(blob, ifMatch, ct).ConfigureAwait(false); }
        catch (BrainSyncConflictException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException) { SetProblem(BrainSyncProblem.Offline); return; }

        lock (_stateLock)
        {
            if (_state is null) return;
            _state.LastKnownRevision = revision;
            _state.LastSyncedUtc = _time.GetUtcNow();
            _state.Dirty = false;
            _state.Problem = BrainSyncProblem.None;
            Save();
        }

        Notify();
    }

    private static bool DocumentsEqual(BrainDocument a, BrainDocument b)
    {
        if (a.Entries.Count != b.Entries.Count) return false;
        var byId = a.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var e in b.Entries)
        {
            if (!byId.TryGetValue(e.Id, out var mine)) return false;
            if (mine.ChangedAt != e.ChangedAt || mine.DeletedAt != e.DeletedAt || mine.Value != e.Value) return false;
        }

        return true;
    }

    private void SetProblem(BrainSyncProblem problem)
    {
        lock (_stateLock)
        {
            if (_state is null) return;
            _state.Problem = problem;
            Save();
        }

        Notify();
    }

    private void Notify() => StatusChanged?.Invoke(this, EventArgs.Empty);

    private void Load()
    {
        try
        {
            var state = _storage.ReadSecret(StateSecretName);
            var key = _storage.ReadSecret(KeySecretName);
            if (state is null || key is null || key.Length != BrainCrypto.KeyLength) return;
            _state = JsonSerializer.Deserialize(state, BrainSyncJsonContext.Default.SyncState);
            _key = _state is null ? null : key;
            // A wrong-password stop is a session problem: the next start asks the server again.
            if (_state is not null && _state.Problem != BrainSyncProblem.WrongPassword) _state.Problem = BrainSyncProblem.None;
        }
        catch (Exception)
        {
            _state = null;
            _key = null;
        }
    }

    private void Save()
    {
        if (_state is null || _key is null) return;
        _storage.WriteSecret(KeySecretName, _key);
        _storage.WriteSecret(StateSecretName, JsonSerializer.SerializeToUtf8Bytes(_state, BrainSyncJsonContext.Default.SyncState));
    }

    /// <summary>The sync state kept next to the key: the salt and parameters of this device's blobs, the revision last seen, dirtiness.</summary>
    public sealed class SyncState
    {
        public BrainKeyParameters Parameters { get; set; } = new(BrainCrypto.DefaultMemoryKiB, BrainCrypto.DefaultIterations, BrainCrypto.DefaultParallelism, new byte[BrainCrypto.SaltLength]);
        public string? LastKnownRevision { get; set; }
        public DateTimeOffset? LastSyncedUtc { get; set; }
        public bool Dirty { get; set; }
        public BrainSyncProblem Problem { get; set; }
        /// <summary>Reserved: a server blob kept aside while the user is asked for its password.</summary>
        public string? PendingServerBlob { get; set; }
    }
}

public enum BrainSyncProblem
{
    None,
    /// <summary>The server could not be reached; the change stays dirty and is uploaded on the next foreground or change.</summary>
    Offline,
    /// <summary>The server copy was encrypted with another password; syncing stopped until the user replaces it or enters the password.</summary>
    WrongPassword,
    /// <summary>The server copy is not a brain blob this app can read.</summary>
    InvalidServerCopy,
    /// <summary>The brain document exceeds the sync cap; nothing is uploaded until it shrinks.</summary>
    TooLarge,
}

public sealed record BrainSyncStatus(bool IsEnabled, string? LastKnownRevision, DateTimeOffset? LastSyncedUtc, bool Dirty, BrainSyncProblem Problem, bool HasPendingServerCopy);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BrainSync.SyncState))]
[JsonSerializable(typeof(BrainKeyParameters))]
internal sealed partial class BrainSyncJsonContext : JsonSerializerContext;
