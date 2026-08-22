using System.Collections.Generic;

namespace Wino.Core.Domain.Models.SemanticIndexing;

public sealed record IntelligenceDownloadResult(
    IReadOnlySet<string> CoveredRemoteMessageIds,
    int RestoredArtifactCount);
