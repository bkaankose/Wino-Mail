using Foundation;
using Security;

namespace Wino.Platform.Apple.Security;

/// <summary>
/// Generic password items through the supported Security bindings. No file fallback.
/// Service names are "&lt;bundle identifier&gt;.v1.&lt;scope&gt;", so they must not change for an existing app.
/// </summary>
public sealed class AppleKeychainStore
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
        var record = new SecRecord(SecKind.GenericPassword) { Service = Service(scope) };
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

    public void Write(string scope, string account, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var query = Query(scope, account);
        using var data = NSData.FromArray(bytes);
        using var attributes = new SecRecord { ValueData = data };
        var status = SecKeyChain.Update(query, attributes);
        if (status == SecStatusCode.ItemNotFound)
        {
            query.ValueData = data;
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
                _ => AppleKeychainFailure.NativeFailure,
            };
            throw new AppleKeychainException(failure, (int)status);
        }
    }
}
