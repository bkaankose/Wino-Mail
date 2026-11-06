using System;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.CardDav;

/// <summary>
/// Which account's certificate trusts apply to a DAV request. Requests without one keep the system validation.
/// </summary>
public sealed class DavEndpointTrust
{
    public Guid AccountId { get; init; }
    public MailServerProtocol Protocol { get; init; }

    /// <summary>A trust the user accepted during setup that is not stored yet.</summary>
    public MailServerCertificateTrust TransientTrust { get; init; }

    public static DavEndpointTrust For(CustomServerInformation serverInformation, MailServerProtocol protocol)
        => new()
        {
            AccountId = serverInformation?.AccountId ?? Guid.Empty,
            Protocol = protocol,
            TransientTrust = serverInformation?.PendingCertificateTrusts?.LastOrDefault(item => item.Protocol == protocol)
        };
}
