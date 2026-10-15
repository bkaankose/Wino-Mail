#nullable enable
using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Security;

/// <summary>
/// The protection key as one secure item ("protection" / "aes256-gcm-v1"). The Apple apps keep it in the Keychain;
/// the scope and account name the existing item, so they must not change.
/// </summary>
public sealed class SecureItemSecretProtectionKeyProvider : ISecretProtectionKeyProvider
{
    public const string KeyScope = "protection";
    public const string KeyAccount = "aes256-gcm-v1";
    private readonly ISecureItemStore _store;

    public SecureItemSecretProtectionKeyProvider(ISecureItemStore store, string applicationIdentity)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationIdentity);
        _store = store;
        ApplicationIdentity = applicationIdentity;
    }

    public string ApplicationIdentity { get; }

    public byte[]? ReadKey() => _store.Read(KeyScope, KeyAccount);

    public bool TryAddKey(byte[] key) => _store.TryAdd(KeyScope, KeyAccount, key);
}
