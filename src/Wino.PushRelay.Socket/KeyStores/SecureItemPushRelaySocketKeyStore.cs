#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.PushRelay.Socket.KeyStores;

/// <summary>
/// The socket key as one secure item (a Keychain item on the Mac), "this device only" so a backup restored onto another
/// Mac does not carry the key, in the same scope as the relay device identity.
/// </summary>
public sealed class SecureItemPushRelaySocketKeyStore(ISecureItemStore store) : PushRelaySocketKeyStoreBase
{
    public const string Scope = "push-relay";
    public const string Account = "socket-key-v1";

    private readonly ISecureItemStore _store = store ?? throw new ArgumentNullException(nameof(store));

    protected override Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.Read(Scope, Account));
    }

    protected override Task WriteAsync(byte[] pkcs8PrivateKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.Write(Scope, Account, pkcs8PrivateKey, SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock);
        return Task.CompletedTask;
    }

    protected override Task DeleteCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.Delete(Scope, Account);
        return Task.CompletedTask;
    }
}
