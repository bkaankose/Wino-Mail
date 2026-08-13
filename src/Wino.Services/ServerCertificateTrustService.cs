using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class ServerCertificateTrustService : BaseDatabaseService, IServerCertificateTrustService
{
    public ServerCertificateTrustService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public Task<MailServerCertificateTrust> GetTrustAsync(Guid accountId, MailServerProtocol protocol, string host, int port)
    {
        var normalizedHost = NormalizeHost(host);
        return Connection.Table<MailServerCertificateTrust>()
            .FirstOrDefaultAsync(item => item.AccountId == accountId &&
                                         item.Protocol == protocol &&
                                         item.Host == normalizedHost &&
                                         item.Port == port);
    }

    public async Task SaveTrustsAsync(Guid accountId, IEnumerable<MailServerCertificateTrust> trusts)
    {
        foreach (var trust in trusts?.Where(item => item != null) ?? [])
        {
            trust.AccountId = accountId;
            trust.Host = NormalizeHost(trust.Host);
            trust.Id = trust.Id == Guid.Empty ? Guid.NewGuid() : trust.Id;

            await Connection.ExecuteAsync(
                $"DELETE FROM {nameof(MailServerCertificateTrust)} WHERE {nameof(MailServerCertificateTrust.AccountId)} = ? AND {nameof(MailServerCertificateTrust.Protocol)} = ? AND {nameof(MailServerCertificateTrust.Host)} = ? AND {nameof(MailServerCertificateTrust.Port)} = ?",
                accountId,
                (int)trust.Protocol,
                trust.Host,
                trust.Port).ConfigureAwait(false);
            await Connection.InsertAsync(trust, typeof(MailServerCertificateTrust)).ConfigureAwait(false);
        }
    }

    public Task DeleteEndpointTrustAsync(Guid accountId, MailServerProtocol protocol, string host, int port)
        => Connection.ExecuteAsync(
            $"DELETE FROM {nameof(MailServerCertificateTrust)} WHERE {nameof(MailServerCertificateTrust.AccountId)} = ? AND {nameof(MailServerCertificateTrust.Protocol)} = ? AND {nameof(MailServerCertificateTrust.Host)} = ? AND {nameof(MailServerCertificateTrust.Port)} = ?",
            accountId,
            (int)protocol,
            NormalizeHost(host),
            port);

    public Task DeleteAccountTrustsAsync(Guid accountId)
        => Connection.Table<MailServerCertificateTrust>().DeleteAsync(item => item.AccountId == accountId);

    private static string NormalizeHost(string host) => host?.Trim().ToLowerInvariant() ?? string.Empty;
}
