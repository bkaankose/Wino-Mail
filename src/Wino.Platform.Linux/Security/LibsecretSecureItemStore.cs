using System.Runtime.InteropServices;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux.Security;

/// <summary>
/// Secure items in the freedesktop Secret Service (GNOME Keyring, KWallet) through libsecret, in the default
/// collection. The attributes name existing items, so the schema name and the attribute names must never change.
/// There is no file fallback: when the service is missing or stays locked, every call throws
/// <see cref="SecretServiceException"/>.
/// </summary>
public sealed unsafe partial class LibsecretSecureItemStore : ISecureItemStore
{
    private const string Secret = "libsecret-1.so.0";
    private const string GLib = "libglib-2.0.so.0";
    private const string SchemaName = "app.winomail.Mail.SecureItem";

    private readonly string _applicationId;
    private readonly nint _schema;

    public LibsecretSecureItemStore(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        _applicationId = applicationId;

        // SECRET_SCHEMA_ATTRIBUTE_STRING is 0, so every attribute maps to a null pointer value.
        using var types = new StringTable(("application", null), ("scope", null), ("account", null));
        _schema = secret_schema_newv(SchemaName, 0, types.Handle);
    }

    public byte[]? Read(string scope, string account)
    {
        using var attributes = Attributes(scope, account);
        nint error = 0;
        var text = secret_password_lookupv_sync(_schema, attributes.Handle, 0, &error);
        ThrowOnError(error, "read");
        if (text == 0) return null;
        try { return Convert.FromBase64String(Marshal.PtrToStringUTF8(text) ?? string.Empty); }
        finally { secret_password_free(text); }
    }

    public void Write(string scope, string account, byte[] bytes, SecureItemAccess access = SecureItemAccess.Default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var attributes = Attributes(scope, account);
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes) + '\0');
        try
        {
            nint error = 0;
            bool stored;
            fixed (byte* password = encoded)
                stored = secret_password_storev_sync(_schema, attributes.Handle, null, $"Wino Mail ({scope})", password, 0, &error) != 0;
            ThrowOnError(error, "write");
            if (!stored) throw new SecretServiceException("The Secret Service did not store the item.");
        }
        finally { Array.Clear(encoded); }
    }

    public bool TryAdd(string scope, string account, byte[] bytes)
    {
        var existing = Read(scope, account);
        if (existing is not null)
        {
            Array.Clear(existing);
            return false;
        }
        Write(scope, account, bytes);
        return true;
    }

    public void Delete(string scope, string account)
    {
        using var attributes = Attributes(scope, account);
        nint error = 0;
        secret_password_clearv_sync(_schema, attributes.Handle, 0, &error);
        ThrowOnError(error, "delete");
    }

    public void DeleteScope(string scope)
    {
        using var attributes = new StringTable(("application", _applicationId), ("scope", scope));
        nint error = 0;
        secret_password_clearv_sync(_schema, attributes.Handle, 0, &error);
        ThrowOnError(error, "delete");
    }

    private StringTable Attributes(string scope, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        return new StringTable(("application", _applicationId), ("scope", scope), ("account", account));
    }

    private static void ThrowOnError(nint error, string operation)
    {
        if (error == 0) return;
        var message = Marshal.PtrToStringUTF8(((GError*)error)->Message);
        g_error_free(error);
        throw new SecretServiceException($"The Secret Service could not {operation} the item: {message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GError
    {
        public uint Domain;
        public int Code;
        public nint Message;
    }

    /// <summary>A GHashTable of UTF-8 strings that owns the strings for its lifetime.</summary>
    private sealed class StringTable : IDisposable
    {
        private static readonly nint Library = NativeLibrary.Load(GLib);
        private static readonly nint StringHash = NativeLibrary.GetExport(Library, "g_str_hash");
        private static readonly nint StringEqual = NativeLibrary.GetExport(Library, "g_str_equal");
        private readonly List<nint> _strings = [];

        public StringTable(params (string Key, string? Value)[] entries)
        {
            Handle = g_hash_table_new(StringHash, StringEqual);
            foreach (var (key, value) in entries)
                g_hash_table_insert(Handle, Allocate(key), value is null ? 0 : Allocate(value));
        }

        public nint Handle { get; }

        public void Dispose()
        {
            g_hash_table_unref(Handle);
            foreach (var text in _strings) Marshal.FreeCoTaskMem(text);
        }

        private nint Allocate(string text)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(text);
            _strings.Add(pointer);
            return pointer;
        }
    }

    [LibraryImport(Secret, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint secret_schema_newv(string name, int flags, nint attributeNamesAndTypes);

    [LibraryImport(Secret)]
    private static partial nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, nint* error);

    [LibraryImport(Secret, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int secret_password_storev_sync(nint schema, nint attributes, string? collection, string label, byte* password, nint cancellable, nint* error);

    [LibraryImport(Secret)]
    private static partial int secret_password_clearv_sync(nint schema, nint attributes, nint cancellable, nint* error);

    [LibraryImport(Secret)]
    private static partial void secret_password_free(nint password);

    [LibraryImport(GLib)]
    private static partial nint g_hash_table_new(nint hash, nint equal);

    [LibraryImport(GLib)]
    private static partial int g_hash_table_insert(nint table, nint key, nint value);

    [LibraryImport(GLib)]
    private static partial void g_hash_table_unref(nint table);

    [LibraryImport(GLib)]
    private static partial void g_error_free(nint error);
}
