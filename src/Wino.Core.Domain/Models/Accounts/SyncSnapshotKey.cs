namespace Wino.Core.Domain.Models.Accounts;

/// <summary>
/// Argon2id inputs written into the snapshot header. Two devices that share them and the secret
/// derive the same key.
/// </summary>
public sealed record SyncSnapshotKeyParameters(byte KeySource, int MemoryKiB, int Iterations, int Parallelism, byte[] Salt);

/// <summary>
/// A derived snapshot key together with the parameters that produced it.
/// </summary>
public sealed record SyncSnapshotKey(SyncSnapshotKeyParameters Parameters, byte[] Key);

/// <summary>
/// The snapshot stored for the account, as downloaded.
/// </summary>
public sealed record WinoSyncSnapshotDownload(byte[] Payload, long Revision);
