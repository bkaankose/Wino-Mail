using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Migration;

namespace Wino.Core.Domain.Interfaces;

public interface IAuthenticationTokenMigrationService
{
    Task<AuthenticationTokenMigrationResult> PrepareAsync(
        IReadOnlyCollection<MigrationAccountOptions> accounts,
        CancellationToken cancellationToken = default);

    Task FinalizeAsync(
        IReadOnlyCollection<MigrationAccountOptions> accounts,
        CancellationToken cancellationToken = default);
}

public sealed record AuthenticationTokenMigrationResult(
    bool OutlookCacheMigrated,
    IReadOnlyCollection<Guid> ReusableGmailAccountIds);
