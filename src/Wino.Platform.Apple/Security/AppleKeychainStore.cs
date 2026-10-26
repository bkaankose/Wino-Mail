using Foundation;
using Security;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Apple.Security;

/// <summary>
/// Generic password items through the supported Security bindings. No file fallback.
/// Service names are "&lt;bundle identifier&gt;.v1.&lt;scope&gt;", so they must not change for an existing app.
/// </summary>
public sealed class AppleKeychainStore : ISecureItemStore
{
    public AppleKeychainStore(AppleApplicationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ApplicationIdentity = identity.Value;
    }

    public string ApplicationIdentity { get; }
    public string Service(string scope) => $"{ApplicationIdentity}.v1.{scope}";

    private SecRecord Query(string scope, string? account)
    {
        // macOS otherwise uses the file-based login keychain, whose per-binary access lists prompt after every new
        // build or update. The data protection keychain grants access by the signed application identifier instead,
        // so unsigned or ad hoc Mac builds get errSecMissingEntitlement. iOS only has this keychain.
        var record = new SecRecord(SecKind.GenericPassword) { Service = Service(scope), UseDataProtectionKeychain = true };
        // SecRecord's string setters reject null; absence is the scope-wide query.
        if (account is not null) record.Account = account;
        return record;
    }

    public byte[]? Read(string scope, string account)
    {
        using var query = Query(scope, account);
        using var data = SecKeyChain.QueryAsData(query, false, out var status);
        if (status == SecStatusCode.ItemNotFound) return null;
        Check(status);
        return data?.ToArray() ?? throw new InvalidOperationException("Keychain returned no credential data.");
    }

    /// <summary>
    /// Writes the item. <see cref="SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock"/> sets
    /// <see cref="SecAccessible.AfterFirstUnlockThisDeviceOnly"/>, for data that must not move to another device through a
    /// backup and must stay readable in background work; an existing item gets the new class too. The default leaves the
    /// accessibility class unchanged.
    /// </summary>
    public void Write(string scope, string account, byte[] bytes, SecureItemAccess access = SecureItemAccess.Default)
        => WriteCore(scope, account, bytes, access switch
        {
            SecureItemAccess.Default => null,
            SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock => SecAccessible.AfterFirstUnlockThisDeviceOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(access)),
        });

    private void WriteCore(string scope, string account, byte[] bytes, SecAccessible? accessible)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var query = Query(scope, account);
        using var data = NSData.FromArray(bytes);
        using var attributes = new SecRecord { ValueData = data };
        if (accessible is { } updatedAccessible) attributes.Accessible = updatedAccessible;
        var status = SecKeyChain.Update(query, attributes);
        if (status == SecStatusCode.ItemNotFound)
        {
            query.ValueData = data;
            if (accessible is { } addedAccessible) query.Accessible = addedAccessible;
            status = SecKeyChain.Add(query);
            if (status == SecStatusCode.DuplicateItem)
            {
                using var retry = Query(scope, account);
                status = SecKeyChain.Update(retry, attributes);
            }
        }
        Check(status);
    }

    public bool TryAdd(string scope, string account, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var record = Query(scope, account);
        using var data = NSData.FromArray(bytes);
        record.ValueData = data;
        var status = SecKeyChain.Add(record);
        if (status == SecStatusCode.DuplicateItem) return false;
        Check(status);
        return true;
    }

    public void Delete(string scope, string account) => DeleteMatching(scope, account);
    public void DeleteScope(string scope) => DeleteMatching(scope, null);

    private void DeleteMatching(string scope, string? account)
    {
        using var query = Query(scope, account);
        var status = SecKeyChain.Remove(query);
        if (status != SecStatusCode.ItemNotFound) Check(status);
    }

    private static void Check(SecStatusCode status)
    {
        if (status != SecStatusCode.Success)
        {
            var failure = (int)status switch
            {
                -25308 => AppleKeychainFailure.InteractionRequired,
                -25293 => AppleKeychainFailure.Denied,
                -25291 => AppleKeychainFailure.Locked,
                -34018 => AppleKeychainFailure.MissingEntitlement,
                _ => AppleKeychainFailure.NativeFailure,
            };
            throw new AppleKeychainException(failure, (int)status);
        }
    }
}
