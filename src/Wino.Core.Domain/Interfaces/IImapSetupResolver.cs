#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Finds the IMAP servers, sign-in options and DAV services for an address. Catalog providers resolve without network
/// calls; other addresses run autodiscovery (with CalDAV and CardDAV discovery in parallel), the IMAP capability probe
/// and OAuth issuer discovery. Network failures end as "not found"; only cancellation throws.
/// </summary>
public interface IImapSetupResolver
{
    Task<ImapSetupResolution> ResolveAsync(
        ImapSetupRequest request,
        IProgress<ImapSetupStage>? progress = null,
        CancellationToken cancellationToken = default);
}
