#nullable enable
using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Exceptions;

namespace Wino.Services.Security;

/// <summary>Host policy for <see cref="SecureItemAccountCredentialPersistence"/>. The defaults keep the strict behavior.</summary>
public sealed class AccountCredentialPersistenceOptions
{
    /// <summary>
    /// A non-empty value without the reference prefix is plaintext an earlier release stored. It is restored as is,
    /// so the account keeps working, and reported through <see cref="LegacyPlaintextFound"/> for migration.
    /// </summary>
    public bool AcceptLegacyPlaintext { get; init; }

    /// <summary>A reference whose item cannot be decrypted or parsed reads as a missing credential.</summary>
    public bool TreatUnreadableAsMissing { get; init; }

    public Action<StoredCredentialKind, Guid>? LegacyPlaintextFound { get; init; }
}

/// <summary>
/// Immutable secure item revisions (Keychain items on Apple); SQLite receives account-bound opaque references only.
/// The reference prefix, scopes and JSON shape are stored data, so they must not change.
/// </summary>
/// <remarks>
/// Revisions never change once written, so decrypted revisions are cached by reference: a new secret is a new
/// reference, and deleting an account's scope or a superseded revision evicts its entries.
/// </remarks>
public sealed class SecureItemAccountCredentialPersistence : IAccountCredentialPersistence
{
    internal const string Prefix = "wino-keychain:v1:";
    private const string MailPurpose = "mail-passwords";
    private const string WinoPurpose = "wino-tokens";

    private readonly ISecureItemStore _store;
    private readonly AccountCredentialPersistenceOptions _options;
    private readonly ConcurrentDictionary<string, string?[]> _revisions = new(StringComparer.Ordinal);
    private readonly ILogger _logger = Log.ForContext<SecureItemAccountCredentialPersistence>();

    public SecureItemAccountCredentialPersistence(ISecureItemStore store) : this(store, new AccountCredentialPersistenceOptions()) { }

    public SecureItemAccountCredentialPersistence(ISecureItemStore store, AccountCredentialPersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
    }

    public Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(information);
        cancellationToken.ThrowIfCancellationRequested();
        var stored = information.CloneForCredentialStorage();
        string?[] secrets = [information.IncomingServerPassword, information.OutgoingServerPassword, information.CalDavPassword];
        var reference = WriteRevision(information.AccountId, MailPurpose, secrets);
        if (reference is not null)
        {
            stored.IncomingServerPassword = stored.OutgoingServerPassword = stored.CalDavPassword = reference;
            _revisions[reference] = secrets;
        }
        return Task.FromResult(stored);
    }

    public Task RestoreServerInformationSecretsAsync(CustomServerInformation information, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(information);
        cancellationToken.ThrowIfCancellationRequested();
        var secrets = ReadRevision(information.AccountId, MailPurpose,
            [information.IncomingServerPassword, information.OutgoingServerPassword, information.CalDavPassword]);
        if (secrets is not null)
        {
            information.IncomingServerPassword = secrets[0];
            information.OutgoingServerPassword = secrets[1];
            information.CalDavPassword = secrets[2];
        }
        return Task.CompletedTask;
    }

    public Task<WinoAccount> PrepareWinoAccountForStorageAsync(WinoAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();
        var stored = account.CloneForCredentialStorage();
        string?[] secrets = [account.AccessToken, account.RefreshToken];
        var reference = WriteRevision(account.Id, WinoPurpose, secrets);
        if (reference is not null)
        {
            stored.AccessToken = stored.RefreshToken = reference;
            _revisions[reference] = secrets;
        }
        return Task.FromResult(stored);
    }

    public Task RestoreWinoAccountSecretsAsync(WinoAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();
        var secrets = ReadRevision(account.Id, WinoPurpose, [account.AccessToken, account.RefreshToken]);
        // WinoAccount tokens are non-null (empty when absent); a revision stores an absent token as null.
        if (secrets is not null) { account.AccessToken = secrets[0] ?? string.Empty; account.RefreshToken = secrets[1] ?? string.Empty; }
        return Task.CompletedTask;
    }

    public Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DeleteScopeAsync(accountId, MailPurpose, cancellationToken);

    public Task DeleteWinoAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DeleteScopeAsync(accountId, WinoPurpose, cancellationToken);

    public Task DeleteSupersededSecretsAsync(Guid accountId, string? previousStoredValue, string? currentStoredValue, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(previousStoredValue) || string.Equals(previousStoredValue, currentStoredValue, StringComparison.Ordinal))
            return Task.CompletedTask;

        foreach (var purpose in (string[])[MailPurpose, WinoPurpose])
        {
            if (TryParseReference(previousStoredValue, accountId, purpose, out var revision))
            {
                DeleteRevision(accountId, purpose, previousStoredValue, revision);
                break;
            }
        }
        return Task.CompletedTask;
    }

    internal static bool IsLegacyPlaintext(string?[] values)
    {
        var hasValue = false;
        foreach (var value in values)
        {
            if (value?.StartsWith(Prefix, StringComparison.Ordinal) == true) return false;
            hasValue |= !string.IsNullOrEmpty(value);
        }
        return hasValue;
    }

    /// <summary>Writes a revision for migration without caching it, so <see cref="VerifyRevision"/> reads the stored item.</summary>
    internal string WriteMigrationRevision(StoredCredentialKind kind, Guid accountId, string?[] secrets)
        => WriteRevision(accountId, PurposeOf(kind), secrets)
           ?? throw new ArgumentException("A migrated credential group must contain a secret.", nameof(secrets));

    /// <summary>Reads the stored item behind <paramref name="reference"/>, bypassing the cache, and compares it with the expected secrets.</summary>
    internal bool VerifyRevision(StoredCredentialKind kind, Guid accountId, string reference, string?[] expected)
    {
        var purpose = PurposeOf(kind);
        if (!TryParseReference(reference, accountId, purpose, out var revision)) return false;

        try
        {
            var stored = ReadStoredRevision(accountId, purpose, revision, expected.Length);
            if (stored is null) return false;
            for (var index = 0; index < expected.Length; index++)
                if (!SecretEquals(stored[index], expected[index])) return false;
            return true;
        }
        catch (CryptographicException exception)
        {
            _logger.Warning(exception, "A migrated credential revision of account {AccountId} could not be read back.", accountId);
            return false;
        }
    }

    internal void DeleteMigrationRevision(StoredCredentialKind kind, Guid accountId, string reference)
    {
        var purpose = PurposeOf(kind);
        if (TryParseReference(reference, accountId, purpose, out var revision))
            DeleteRevision(accountId, purpose, reference, revision);
    }

    private Task DeleteScopeAsync(Guid accountId, string purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _store.DeleteScope(Scope(accountId, purpose));
        var referencePrefix = ReferencePrefix(accountId, purpose);
        foreach (var reference in _revisions.Keys)
            if (reference.StartsWith(referencePrefix, StringComparison.Ordinal))
                _revisions.TryRemove(reference, out _);
        return Task.CompletedTask;
    }

    private void DeleteRevision(Guid accountId, string purpose, string reference, Guid revision)
    {
        _revisions.TryRemove(reference, out _);
        try { _store.Delete(Scope(accountId, purpose), revision.ToString("N")); }
        catch (Exception exception)
        {
            // A leftover revision is unreferenced data; the update that superseded it already succeeded.
            _logger.Warning(exception, "A superseded credential revision of account {AccountId} could not be deleted.", accountId);
        }
    }

    private string? WriteRevision(Guid accountId, string purpose, string?[] secrets)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("Credential account identity is required.");
        var present = false;
        foreach (var secret in secrets)
        {
            if (secret?.StartsWith(Prefix, StringComparison.Ordinal) == true)
                throw new CryptographicException("A storage reference must be hydrated before updating credentials.");
            present |= !string.IsNullOrEmpty(secret);
        }
        if (!present) return null;

        var revision = Guid.NewGuid().ToString("N");
        var envelope = new AccountCredentialRevision { AccountId = accountId, Purpose = purpose, Secrets = secrets };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, AccountCredentialJsonContext.Default.AccountCredentialRevision);
        try
        {
            if (!_store.TryAdd(Scope(accountId, purpose), revision, bytes))
                throw new InvalidOperationException("A credential revision identity collision occurred.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return $"{ReferencePrefix(accountId, purpose)}{revision}";
    }

    private string?[]? ReadRevision(Guid accountId, string purpose, string?[] references)
    {
        var hasValue = false;
        foreach (var reference in references) hasValue |= !string.IsNullOrEmpty(reference);
        if (!hasValue) return null;

        if (_options.AcceptLegacyPlaintext && IsLegacyPlaintext(references))
        {
            _options.LegacyPlaintextFound?.Invoke(purpose == MailPurpose ? StoredCredentialKind.MailServerPasswords : StoredCredentialKind.WinoAccountTokens, accountId);
            return null;
        }

        var referenceValue = references[0];
        if (referenceValue is null || !TryParseReference(referenceValue, accountId, purpose, out var revision))
            throw new CryptographicException("The database credential reference is invalid for this account and purpose.");
        foreach (var reference in references)
            if (!string.Equals(reference, referenceValue, StringComparison.Ordinal))
                throw new CryptographicException("The database credential group contains inconsistent revisions.");

        if (_revisions.TryGetValue(referenceValue, out var cached))
            return (string?[])cached.Clone();

        string?[] secrets;
        try
        {
            secrets = ReadStoredRevision(accountId, purpose, revision, references.Length) ?? throw new AccountCredentialMissingException();
        }
        catch (CryptographicException exception) when (_options.TreatUnreadableAsMissing)
        {
            _logger.Warning(exception, "The stored credential of account {AccountId} cannot be read; sign-in is required.", accountId);
            throw new AccountCredentialMissingException();
        }

        _revisions[referenceValue] = secrets;
        return (string?[])secrets.Clone();
    }

    private string?[]? ReadStoredRevision(Guid accountId, string purpose, Guid revision, int expectedLength)
    {
        var bytes = _store.Read(Scope(accountId, purpose), revision.ToString("N"));
        if (bytes is null) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize(bytes, AccountCredentialJsonContext.Default.AccountCredentialRevision);
            if (envelope is null || envelope.AccountId != accountId || envelope.Purpose != purpose ||
                envelope.Secrets is null || envelope.Secrets.Length != expectedLength)
                throw new CryptographicException("The stored credential revision does not match its database reference.");
            foreach (var secret in envelope.Secrets)
                if (secret?.StartsWith(Prefix, StringComparison.Ordinal) == true)
                    throw new CryptographicException("The stored credential revision contains a storage reference.");
            return envelope.Secrets;
        }
        catch (JsonException exception) { throw new CryptographicException("The stored credential revision is corrupt.", exception); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool TryParseReference(string reference, Guid accountId, string purpose, out Guid revision)
    {
        var expectedPrefix = ReferencePrefix(accountId, purpose);
        revision = Guid.Empty;
        return reference.StartsWith(expectedPrefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(reference[expectedPrefix.Length..], "N", out revision);
    }

    private static bool SecretEquals(string? stored, string? expected)
    {
        if (stored is null || expected is null) return stored is null && expected is null;
        var storedBytes = Encoding.UTF8.GetBytes(stored);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        try { return CryptographicOperations.FixedTimeEquals(storedBytes, expectedBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(storedBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    private static string PurposeOf(StoredCredentialKind kind) => kind switch
    {
        StoredCredentialKind.MailServerPasswords => MailPurpose,
        StoredCredentialKind.WinoAccountTokens => WinoPurpose,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string ReferencePrefix(Guid accountId, string purpose) => $"{Prefix}{purpose}:{accountId:N}:";

    private static string Scope(Guid accountId, string purpose) => $"{purpose}.{accountId:N}";
}

internal sealed class AccountCredentialRevision
{
    public Guid AccountId { get; set; }
    public string? Purpose { get; set; }
    public string?[]? Secrets { get; set; }
}

[JsonSerializable(typeof(AccountCredentialRevision))]
internal partial class AccountCredentialJsonContext : JsonSerializerContext;
