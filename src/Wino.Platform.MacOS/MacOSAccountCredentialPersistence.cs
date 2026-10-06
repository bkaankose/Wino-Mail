#nullable enable
using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.MacOS.Security;

namespace Wino.Platform.MacOS;

/// <summary>Immutable Keychain revisions; SQLite receives account-bound opaque references only.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSAccountCredentialPersistence(MacOSKeychainStore keychain) : IAccountCredentialPersistence
{
    private const string Prefix = "wino-keychain:v1:";
    private const string MailPurpose = "mail-passwords";
    private const string WinoPurpose = "wino-tokens";

    public Task<CustomServerInformation> PrepareServerInformationForStorageAsync(CustomServerInformation information, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(information);
        cancellationToken.ThrowIfCancellationRequested();
        var stored = information.CloneForCredentialStorage();
        var reference = WriteRevision(information.AccountId, MailPurpose,
            [information.IncomingServerPassword, information.OutgoingServerPassword, information.CalDavPassword]);
        if (reference is not null)
            stored.IncomingServerPassword = stored.OutgoingServerPassword = stored.CalDavPassword = reference;
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
        var reference = WriteRevision(account.Id, WinoPurpose, [account.AccessToken, account.RefreshToken]);
        if (reference is not null) stored.AccessToken = stored.RefreshToken = reference;
        return Task.FromResult(stored);
    }

    public Task RestoreWinoAccountSecretsAsync(WinoAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        cancellationToken.ThrowIfCancellationRequested();
        var secrets = ReadRevision(account.Id, WinoPurpose, [account.AccessToken, account.RefreshToken]);
        if (secrets is not null) { account.AccessToken = secrets[0]; account.RefreshToken = secrets[1]; }
        return Task.CompletedTask;
    }

    public Task DeleteMailAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DeleteScopeAsync(accountId, MailPurpose, cancellationToken);

    public Task DeleteWinoAccountSecretsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => DeleteScopeAsync(accountId, WinoPurpose, cancellationToken);

    private Task DeleteScopeAsync(Guid accountId, string purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        keychain.DeleteScope(Scope(accountId, purpose));
        return Task.CompletedTask;
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
            if (!keychain.TryAdd(Scope(accountId, purpose), revision, bytes))
                throw new InvalidOperationException("A credential revision identity collision occurred.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return $"{Prefix}{purpose}:{accountId:N}:{revision}";
    }

    private string?[]? ReadRevision(Guid accountId, string purpose, string?[] references)
    {
        var hasValue = false;
        foreach (var reference in references) hasValue |= !string.IsNullOrEmpty(reference);
        if (!hasValue) return null;
        var expectedPrefix = $"{Prefix}{purpose}:{accountId:N}:";
        var referenceValue = references[0];
        if (referenceValue is null || !referenceValue.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(referenceValue[expectedPrefix.Length..], "N", out var revision))
            throw new CryptographicException("The database credential reference is invalid for this account and purpose.");
        foreach (var reference in references)
            if (!string.Equals(reference, referenceValue, StringComparison.Ordinal))
                throw new CryptographicException("The database credential group contains inconsistent revisions.");

        var bytes = keychain.Read(Scope(accountId, purpose), revision.ToString("N")) ?? throw new MacOSCredentialMissingException();
        try
        {
            var envelope = JsonSerializer.Deserialize(bytes, AccountCredentialJsonContext.Default.AccountCredentialRevision);
            if (envelope is null || envelope.AccountId != accountId || envelope.Purpose != purpose ||
                envelope.Secrets is null || envelope.Secrets.Length != references.Length)
                throw new CryptographicException("The Keychain credential revision does not match its database reference.");
            foreach (var secret in envelope.Secrets)
                if (secret?.StartsWith(Prefix, StringComparison.Ordinal) == true)
                    throw new CryptographicException("The Keychain credential revision contains a storage reference.");
            return envelope.Secrets;
        }
        catch (JsonException exception) { throw new CryptographicException("The Keychain credential revision is corrupt.", exception); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

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
