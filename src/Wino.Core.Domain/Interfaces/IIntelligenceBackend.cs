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
}
