#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.PushRelay.Socket.KeyStores;

/// <summary>
/// The shared shape of a socket key store: the platform keeps the PKCS#8 bytes, this class validates them and
/// generates a key when none is usable. One key per install; it is never rotated by the client.
/// </summary>
public abstract class PushRelaySocketKeyStoreBase : IPushRelaySocketKeyStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<PushRelaySocketDeviceKey> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = PushRelaySocketDeviceKeys.TryFromPkcs8(await ReadAsync(cancellationToken).ConfigureAwait(false));
            if (stored is not null)
                return stored;

            var created = PushRelaySocketDeviceKeys.Generate();
            await WriteAsync(created.Pkcs8PrivateKey, cancellationToken).ConfigureAwait(false);
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PushRelaySocketDeviceKey?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return PushRelaySocketDeviceKeys.TryFromPkcs8(await ReadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The stored PKCS#8 bytes, or null when nothing is stored. Unreadable storage may throw.</summary>
    protected abstract Task<byte[]?> ReadAsync(CancellationToken cancellationToken);
    protected abstract Task WriteAsync(byte[] pkcs8PrivateKey, CancellationToken cancellationToken);
    protected abstract Task DeleteCoreAsync(CancellationToken cancellationToken);
}
