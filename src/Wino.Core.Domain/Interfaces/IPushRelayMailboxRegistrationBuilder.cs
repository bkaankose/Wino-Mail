#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Builds the relay mailbox registration for accounts of one provider type.</summary>
public interface IPushRelayMailboxRegistrationBuilder
{
    MailProviderType ProviderType { get; }

    Task<bool> IsEligibleAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>The registration for the account, or null when it is not eligible.</summary>
    Task<PushRelayMailboxRegistrationRequest?> BuildAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Best effort: deletes provider registrations the relay retired for the account (<c>obsoleteExternalIds</c>).
    /// Only app-managed providers have any; failures are logged, never thrown.
    /// </summary>
    Task DeleteObsoleteRegistrationsAsync(MailAccount account, IReadOnlyList<string> externalIds, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Best effort: deletes the account's own provider registration and forgets it, so the provider stops notifying
    /// the relay (the account is removed or the relay is turned off). Failures are logged, never thrown.
    /// </summary>
    Task ReleaseRegistrationAsync(MailAccount account, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
