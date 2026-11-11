#if DEBUG
using System.Text.Json;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Debug builds re-bundled as the local lab app (bundle id ending in ".lab", signed ad hoc) cannot reach the data
/// protection keychain, which needs a team-signed application identifier. They keep their secrets in a file in their
/// own sandbox container instead: lab passwords and local API tokens only. Release builds and the normal Debug bundle
/// never use this store.
/// </summary>
internal sealed class LabSecureItemStore : ISecureItemStore
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wino-lab-secrets.json");
    private Dictionary<string, string>? _items;

    public static bool IsLabBundle => Foundation.NSBundle.MainBundle.BundleIdentifier?.EndsWith(".lab", StringComparison.Ordinal) == true;

    public byte[]? Read(string scope, string account)
    {
        lock (_gate) return Items.TryGetValue(Key(scope, account), out var value) ? Convert.FromBase64String(value) : null;
    }

    public void Write(string scope, string account, byte[] bytes, SecureItemAccess access = SecureItemAccess.Default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            Items[Key(scope, account)] = Convert.ToBase64String(bytes);
            Save();
        }
    }

    public bool TryAdd(string scope, string account, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            if (!Items.TryAdd(Key(scope, account), Convert.ToBase64String(bytes))) return false;
            Save();
            return true;
        }
    }

    public void Delete(string scope, string account)
    {
        lock (_gate)
        {
            if (Items.Remove(Key(scope, account))) Save();
        }
    }

    public void DeleteScope(string scope)
    {
        lock (_gate)
        {
            foreach (var key in Items.Keys.Where(k => k.StartsWith(scope + "\n", StringComparison.Ordinal)).ToList()) Items.Remove(key);
            Save();
        }
    }

    private static string Key(string scope, string account) => scope + "\n" + account;

    private Dictionary<string, string> Items => _items ??= File.Exists(_path)
        ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? []
        : [];

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_items));
    }
}
#endif
