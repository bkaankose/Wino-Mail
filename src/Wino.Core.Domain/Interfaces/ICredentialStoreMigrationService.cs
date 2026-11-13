#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public enum StoredCredentialKind
{
    MailServerPasswords,
    WinoAccountTokens
}

/// <summary>
/// Moves credentials an earlier release kept outside the secure item store (plaintext database columns, legacy files)
/// into it. Only hosts that had such a release register it; elsewhere it is absent.
/// </summary>
public interface ICredentialStoreMigrationService
{
    /// <summary>Runs once per launch after the database is ready and before the first synchronizer; never throws for a failed item.</summary>
    Task MigrateAsync(CancellationToken cancellationToken = default);

    /// <summary>A read found plaintext that is still in the database; schedules one retry per account and process.</summary>
    void ReportLegacyPlaintext(StoredCredentialKind kind, Guid accountId);
}

/// <summary>A legacy credential file source that can import its files into the secure item store.</summary>
public interface ILegacyCredentialFileMigration
{
    Task MigrateLegacyFilesAsync(CancellationToken cancellationToken = default);
}
