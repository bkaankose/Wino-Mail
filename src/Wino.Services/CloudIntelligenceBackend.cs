using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

public sealed class CloudIntelligenceBackend(IWinoAccountApiClient apiClient) : IIntelligenceBackend
{
    public IntelligenceBackendKind Kind => IntelligenceBackendKind.Cloud;
    public bool UsesLocalVectorStore => false;

    public Task<IntelligenceIngestResultDto> IngestAsync(Guid mailboxId, byte[] encryptedEnvelope, CancellationToken cancellationToken = default)
        => apiClient.IngestIntelligenceAsync(mailboxId, encryptedEnvelope, cancellationToken);

    public Task<IntelligenceSemanticSearchResultDto> SearchAsync(byte[] encryptedEnvelope, CancellationToken cancellationToken = default)
        => apiClient.SearchIntelligenceAsync(encryptedEnvelope, cancellationToken);
}
