using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MimeKit.Cryptography;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using BcCertificate = Org.BouncyCastle.X509.X509Certificate;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// S/MIME certificates in Wino's own store (decided 2026-10-08: no Keychain import, so certificates do not
/// appear in Keychain Access). Layout under <c>&lt;ApplicationData&gt;/smime</c>:
/// <list type="bullet">
/// <item><c>managed.list</c> — the <see cref="SmimeManagedThumbprintList"/> index (thumbprints and purposes, not secret).</item>
/// <item><c>personal/&lt;THUMBPRINT&gt;.bin</c>, <c>recipient/&lt;THUMBPRINT&gt;.bin</c> — one entry each, sealed with the
/// app's <see cref="ISecretProtector"/> (AES-GCM, key in the Keychain). A key-bearing entry is the leaf, its chain
/// and its private key re-wrapped as PKCS#12 under a random password; a public entry is the DER certificate.</item>
/// </list>
/// Parsing is BouncyCastle-only: macOS would otherwise import PKCS#12 keys into a temporary keychain.
/// </summary>
public sealed class MacSmimeCertificateService(IApplicationConfiguration configuration, ISecretProtector protector) : ISmimeCertificateService
{
    private const string ListFileName = "managed.list";
    private const byte Pkcs12Kind = 1;
    private const byte CertificateKind = 2;
    private readonly object _gate = new();
    private List<MacSmimeStoreEntry>? _entries;

    private string Root => Path.Combine(configuration.ApplicationDataFolderPath, "smime");

    public SecureMimeContext CreateContext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new MacSecureMimeContext(Snapshot(), cancellationToken);
    }

    public IReadOnlyList<X509Certificate2> GetCertificates(SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, string? emailAddress = null, CancellationToken cancellationToken = default)
    {
        var results = new List<X509Certificate2>();
        var parser = new X509CertificateParser();
        foreach (var entry in Snapshot().Where(entry => entry.Purpose == purpose))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (emailAddress is not null && !Matches(parser.ReadCertificate(entry.LeafDer), emailAddress)) continue;
                results.Add(X509CertificateLoader.LoadCertificate(entry.LeafDer));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // An unreadable certificate is left out of the list; it must not break the settings page.
            }
        }
        return results;
    }

    public void ImportCertificate(string fileExtension, byte[] rawData, string? password = null, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawData);
        cancellationToken.ThrowIfCancellationRequested();
        var extension = (fileExtension ?? string.Empty).Trim().ToLowerInvariant();
        if (extension.Length > 0 && extension[0] != '.') extension = "." + extension;

        var entries = extension is ".pfx" or ".p12"
            ? ReadPkcs12(rawData, password, purpose)
            : ReadPublicCertificates(rawData, purpose);
        if (entries.Count == 0) throw new InvalidDataException("The file contains no certificate.");

        lock (_gate)
        {
            var list = LoadList();
            var loaded = LoadEntries();
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteEntry(entry);
                list.Add(entry.Thumbprint, entry.Purpose);
                loaded.RemoveAll(existing => existing.Thumbprint == entry.Thumbprint && existing.Purpose == entry.Purpose);
                loaded.Add(entry);
            }
            SaveList(list);
        }
    }

    public void RemoveCertificate(string thumbprint, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SmimeManagedThumbprintList.TryNormalize(thumbprint, out var normalized)) return;
        lock (_gate)
        {
            var list = LoadList();
            if (list.Remove(normalized, purpose)) SaveList(list);
            var path = EntryPath(normalized, purpose);
            if (File.Exists(path)) File.Delete(path);
            LoadEntries().RemoveAll(entry => entry.Thumbprint == normalized && entry.Purpose == purpose);
        }
    }

    #region Reading imported files

    /// <summary>Each key entry becomes one personal entry: leaf, chain and key re-wrapped under a random password.</summary>
    private static List<MacSmimeStoreEntry> ReadPkcs12(byte[] rawData, string? password, SmimeCertificatePurpose purpose)
    {
        var source = new Pkcs12StoreBuilder().Build();
        using (var input = new MemoryStream(rawData, writable: false))
            source.Load(input, (password ?? string.Empty).ToCharArray());

        var entries = new List<MacSmimeStoreEntry>();
        var random = new SecureRandom();
        foreach (var alias in source.Aliases.ToList())
        {
            if (!source.IsKeyEntry(alias)) continue;
            var chain = source.GetCertificateChain(alias);
            if (chain is null || chain.Length == 0) continue;

            var leaf = chain[0].Certificate.GetEncoded();
            var target = new Pkcs12StoreBuilder().Build();
            target.SetKeyEntry("wino", source.GetKey(alias), chain);
            var storePassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            using var output = new MemoryStream();
            target.Save(output, storePassword.ToCharArray(), random);
            entries.Add(new MacSmimeStoreEntry(Thumbprint(leaf), purpose, true, storePassword, output.ToArray(), leaf));
        }

        // A PKCS#12 file without keys still carries certificates; keep them as public entries.
        if (entries.Count == 0)
        {
            foreach (var alias in source.Aliases.ToList())
            {
                if (source.GetCertificate(alias)?.Certificate is not { } certificate) continue;
                var der = certificate.GetEncoded();
                entries.Add(new MacSmimeStoreEntry(Thumbprint(der), purpose, false, string.Empty, der, der));
            }
        }
        return entries;
    }

    /// <summary>DER, PEM or PKCS#7 (.cer, .crt, .pem, .p7b): every certificate in the file.</summary>
    private static List<MacSmimeStoreEntry> ReadPublicCertificates(byte[] rawData, SmimeCertificatePurpose purpose)
    {
        var entries = new List<MacSmimeStoreEntry>();
        foreach (var certificate in new X509CertificateParser().ReadCertificates(rawData))
        {
            var der = certificate.GetEncoded();
            entries.Add(new MacSmimeStoreEntry(Thumbprint(der), purpose, false, string.Empty, der, der));
        }
        return entries;
    }

    /// <summary>Matches the subject e-mail (E= or the rfc822 alternative name) first, then any mention in the subject.</summary>
    private static bool Matches(BcCertificate certificate, string emailAddress)
    {
        var address = emailAddress.Trim();
        if (address.Length == 0) return true;
        try
        {
            if (string.Equals(certificate.GetSubjectEmailAddress(false), address, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception) { /* Malformed names fall through to the subject check. */ }
        return certificate.SubjectDN?.ToString()?.Contains(address, StringComparison.OrdinalIgnoreCase) == true;
    }

#pragma warning disable CA5350 // SHA-1 here is the certificate thumbprint identity, matching X509Certificate2.Thumbprint.
    private static string Thumbprint(byte[] der) => Convert.ToHexString(SHA1.HashData(der));
#pragma warning restore CA5350

    #endregion

    #region Store files

    private List<MacSmimeStoreEntry> Snapshot()
    {
        lock (_gate) return [.. LoadEntries()];
    }

    /// <summary>Decrypts every listed entry once per process; unreadable entries are skipped, not deleted.</summary>
    private List<MacSmimeStoreEntry> LoadEntries()
    {
        if (_entries is not null) return _entries;
        var entries = new List<MacSmimeStoreEntry>();
        foreach (var listed in LoadList().Entries)
        {
            try
            {
                if (ReadEntry(listed.Thumbprint, listed.Purpose) is { } entry) entries.Add(entry);
            }
            catch (Exception) { /* A damaged or foreign entry must not hide the others. */ }
        }
        return _entries = entries;
    }

    private SmimeManagedThumbprintList LoadList()
    {
        var path = Path.Combine(Root, ListFileName);
        return File.Exists(path) ? SmimeManagedThumbprintList.Parse(File.ReadAllText(path, Encoding.UTF8)) : new SmimeManagedThumbprintList();
    }

    private void SaveList(SmimeManagedThumbprintList list)
    {
        Directory.CreateDirectory(Root);
        WriteAtomically(Path.Combine(Root, ListFileName), Encoding.UTF8.GetBytes(list.Serialize()));
    }

    private string EntryPath(string thumbprint, SmimeCertificatePurpose purpose)
        => Path.Combine(Root, purpose == SmimeCertificatePurpose.Personal ? "personal" : "recipient", thumbprint + ".bin");

    private static byte[] Context(string thumbprint, SmimeCertificatePurpose purpose)
        => Encoding.UTF8.GetBytes($"wino.smime.v1|{purpose}|{thumbprint}");

    /// <summary>Sealed layout: kind (1 byte), password length (int32 LE), password (UTF-8), payload.</summary>
    private void WriteEntry(MacSmimeStoreEntry entry)
    {
        var password = Encoding.UTF8.GetBytes(entry.StorePassword);
        var clear = new byte[1 + 4 + password.Length + entry.Payload.Length];
        var context = Context(entry.Thumbprint, entry.Purpose);
        try
        {
            clear[0] = entry.HasPrivateKey ? Pkcs12Kind : CertificateKind;
            BinaryPrimitives.WriteInt32LittleEndian(clear.AsSpan(1, 4), password.Length);
            password.CopyTo(clear, 5);
            entry.Payload.CopyTo(clear, 5 + password.Length);
            var sealedBytes = protector.Protect(clear, context);
            var path = EntryPath(entry.Thumbprint, entry.Purpose);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WriteAtomically(path, sealedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(password);
            CryptographicOperations.ZeroMemory(context);
        }
    }

    private MacSmimeStoreEntry? ReadEntry(string thumbprint, SmimeCertificatePurpose purpose)
    {
        var path = EntryPath(thumbprint, purpose);
        if (!File.Exists(path)) return null;
        var context = Context(thumbprint, purpose);
        byte[]? clear = null;
        try
        {
            clear = protector.Unprotect(File.ReadAllBytes(path), context);
            if (clear.Length < 5) return null;
            var kind = clear[0];
            var passwordLength = BinaryPrimitives.ReadInt32LittleEndian(clear.AsSpan(1, 4));
            if (passwordLength < 0 || passwordLength > clear.Length - 5) return null;
            var password = Encoding.UTF8.GetString(clear, 5, passwordLength);
            var payload = clear.AsSpan(5 + passwordLength).ToArray();

            byte[] leaf;
            if (kind == Pkcs12Kind)
            {
                var store = new Pkcs12StoreBuilder().Build();
                using (var input = new MemoryStream(payload, writable: false)) store.Load(input, password.ToCharArray());
                var alias = store.Aliases.FirstOrDefault(store.IsKeyEntry);
                var chain = alias is null ? null : store.GetCertificateChain(alias);
                if (chain is null || chain.Length == 0) return null;
                leaf = chain[0].Certificate.GetEncoded();
            }
            else if (kind == CertificateKind)
            {
                leaf = payload;
            }
            else return null;

            return new MacSmimeStoreEntry(thumbprint, purpose, kind == Pkcs12Kind, password, payload, leaf);
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(context);
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    #endregion
}
