#nullable enable
using System;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Device-local memory of the Gmail watches and Graph subscriptions this install created for the relay, so a pass
/// renews them only when they are about to expire. A <c>mailbox.renew</c> push marks one due immediately.
/// </summary>
public interface IPushRelayAppManagedRegistrationStore
{
    /// <summary>The next build for this account renews the provider registration regardless of its stored expiry.</summary>
    void MarkRenewalDue(Guid accountId);

    /// <summary>Forgets the stored registration (the account was removed or re-authenticated).</summary>
    void Forget(Guid accountId);
}
