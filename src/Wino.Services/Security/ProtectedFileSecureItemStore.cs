#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Security;

/// <summary>
/// <see cref="ISecureItemStore"/> as protected files, for hosts without a Keychain (Android). Each scope is a folder
/// and each account a file inside it, both named by the lowercase hex SHA-256 of the UTF-8 name, so names never leak
/// and never need escaping. File contents are <see cref="ISecretProtector"/> envelopes bound to the scope and account
/// (see <see cref="CreateContext"/>), so a file moved to another name does not read. Writes go to a temporary file
/// in the same folder and replace the item with a rename. File names and the context layout are stored data, so they
/// must never change for version 1.
/// </summary>
/// <remarks>
/// <see cref="SecureItemAccess"/> does not change how an item is stored: the host must put <c>root</c> in storage
/// that is device-bound, excluded from backups and readable after the first unlock (on Android, the app's
/// credential-encrypted <c>no_backup</c> folder with Auto Backup off), which is what
/// <see cref="SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock"/> asks for and is stricter than the default.
/// The protector must not keep its own key in this store.
/// </remarks>
public sealed class ProtectedFileSecureItemStore : ISecureItemStore
{
    private const string ItemExtension = ".item";
    private const string TemporaryExtension = ".tmp";
    private static readonly byte[] ContextPrefix = "wino-secure-item:v1"u8.ToArray();
    private readonly string _root;
    private readonly ISecretProtector _protector;

    public ProtectedFileSecureItemStore(string root, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(protector);
        _root = Path.GetFullPath(root);
        _protector = protector;
    }

    public byte[]? Read(string scope, string account)
    {
        var path = ItemPath(scope, account);
        byte[] envelope;
        try { envelope = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var context = CreateContext(scope, account);
        try { return _protector.Unprotect(envelope, context); }
        finally { CryptographicOperations.ZeroMemory(context); }
    }

    public void Write(string scope, string account, byte[] bytes, SecureItemAccess access = SecureItemAccess.Default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (access is not (SecureItemAccess.Default or SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock))
            throw new ArgumentOutOfRangeException(nameof(access));
        var temporary = WriteTemporary(scope, account, bytes);
        try { File.Move(temporary, ItemPath(scope, account), overwrite: true); }
        catch { TryDelete(temporary); throw; }
    }

    public bool TryAdd(string scope, string account, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var path = ItemPath(scope, account);
        if (File.Exists(path)) return false;
        var temporary = WriteTemporary(scope, account, bytes);
        try
        {
            // Without overwrite the move fails when another writer added the item first; that item is kept.
            File.Move(temporary, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            TryDelete(temporary);
            return false;
        }
        catch { TryDelete(temporary); throw; }
    }

    public void Delete(string scope, string account)
    {
        var path = ItemPath(scope, account);
        if (Directory.Exists(Path.GetDirectoryName(path))) File.Delete(path);
    }

    public void DeleteScope(string scope)
    {
        var folder = ScopePath(scope);
        try { Directory.Delete(folder, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>The folder of <paramref name="scope"/> below the root.</summary>
    public string ScopePath(string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        return Path.Combine(_root, HashName(scope));
    }

    /// <summary>The file of the item below the root.</summary>
    public string ItemPath(string scope, string account)
    {
        ArgumentException.ThrowIfNullOrEmpty(account);
        return Path.Combine(ScopePath(scope), HashName(account) + ItemExtension);
    }

    /// <summary>Lowercase hex SHA-256 of the UTF-8 name: 64 characters, safe on every file system.</summary>
    public static string HashName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
    }

    /// <summary>
    /// The protector context of an item: "wino-secure-item:v1", then the scope and the account, each as a little-endian
    /// 32-bit byte length and its UTF-8 bytes.
    /// </summary>
    public static byte[] CreateContext(string scope, string account)
    {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentException.ThrowIfNullOrEmpty(account);
        var scopeBytes = Encoding.UTF8.GetBytes(scope);
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var context = new byte[checked(ContextPrefix.Length + 8 + scopeBytes.Length + accountBytes.Length)];
        ContextPrefix.CopyTo(context, 0);
        var offset = ContextPrefix.Length;
        BinaryPrimitives.WriteInt32LittleEndian(context.AsSpan(offset, 4), scopeBytes.Length);
        scopeBytes.CopyTo(context, offset + 4);
        offset += 4 + scopeBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(context.AsSpan(offset, 4), accountBytes.Length);
        accountBytes.CopyTo(context, offset + 4);
        return context;
    }

    private string WriteTemporary(string scope, string account, byte[] bytes)
    {
        var folder = ScopePath(scope);
        Directory.CreateDirectory(folder);
        var context = CreateContext(scope, account);
        byte[] envelope;
        try { envelope = _protector.Protect(bytes, context); }
        finally { CryptographicOperations.ZeroMemory(context); }

        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + TemporaryExtension);
        try
        {
            using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(envelope);
            stream.Flush(flushToDisk: true);
            return temporary;
        }
        catch { TryDelete(temporary); throw; }
        finally { CryptographicOperations.ZeroMemory(envelope); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
