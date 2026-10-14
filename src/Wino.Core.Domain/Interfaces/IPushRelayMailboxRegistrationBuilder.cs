#nullable enable
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
}
