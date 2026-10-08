using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public sealed class CloudIntelligenceBackend : IIntelligenceBackend
{
    public IntelligenceBackendKind Kind => IntelligenceBackendKind.Cloud;
    public bool UsesLocalVectorStore => true;
}
