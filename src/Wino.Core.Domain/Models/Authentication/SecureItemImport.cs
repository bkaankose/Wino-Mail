#nullable enable
using System;
using System.Security.Cryptography;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Authentication;

public enum SecureItemImportResult
{
    /// <summary>The item was added and read back unchanged.</summary>
    Imported,

    /// <summary>A readable item already existed (or another writer replaced the new one); it is authoritative.</summary>
    ExistingItemKept,

    /// <summary>The added item did not read back and was removed again.</summary>
    ReadBackFailed
}

/// <summary>Imports a legacy credential into a secure item store without ever replacing an existing item.</summary>
public static class SecureItemImport
{
    /// <summary>Store write failures propagate, so the caller keeps its legacy copy.</summary>
    public static SecureItemImportResult Import(ISecureItemStore store, string scope, string account, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(bytes);

        if (!store.TryAdd(scope, account, bytes))
            return SecureItemImportResult.ExistingItemKept;

        byte[]? stored;
        try { stored = store.Read(scope, account); }
        catch (CryptographicException) { stored = null; }

        if (stored is not null)
        {
            try
            {
                // A readable item that differs was written by a concurrent writer after the add; it is newer.
                return CryptographicOperations.FixedTimeEquals(stored, bytes)
                    ? SecureItemImportResult.Imported
                    : SecureItemImportResult.ExistingItemKept;
            }
            finally { CryptographicOperations.ZeroMemory(stored); }
        }

        try { store.Delete(scope, account); }
        catch (Exception) { }
        return SecureItemImportResult.ReadBackFailed;
    }
}
