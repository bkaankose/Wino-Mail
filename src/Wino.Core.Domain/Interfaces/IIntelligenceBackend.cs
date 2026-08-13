using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Interfaces;

public enum IntelligenceBackendKind
{
    Cloud,
    LocalNpu,
}

public interface IIntelligenceBackend
{
    IntelligenceBackendKind Kind { get; }
    bool UsesLocalVectorStore { get; }
    Task<IntelligenceIngestResultDto> IngestAsync(Guid mailboxId, byte[] encryptedEnvelope, CancellationToken cancellationToken = default);
    Task<IntelligenceSemanticSearchResultDto> SearchAsync(byte[] encryptedEnvelope, CancellationToken cancellationToken = default);
}
