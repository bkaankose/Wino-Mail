using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface IServerCertificateTrustService
{
    Task<MailServerCertificateTrust> GetTrustAsync(Guid accountId, MailServerProtocol protocol, string host, int port);
    Task SaveTrustsAsync(Guid accountId, IEnumerable<MailServerCertificateTrust> trusts);
    Task DeleteEndpointTrustAsync(Guid accountId, MailServerProtocol protocol, string host, int port);
    Task DeleteAccountTrustsAsync(Guid accountId);
}
