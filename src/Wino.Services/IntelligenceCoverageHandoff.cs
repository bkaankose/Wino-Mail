#nullable enable
using System.Threading;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Services;

/// <inheritdoc cref="IIntelligenceCoverageHandoff"/>
public sealed class IntelligenceCoverageHandoff : IIntelligenceCoverageHandoff
{
    private IntelligenceCoverageResult? _pending;

    public void Publish(IntelligenceCoverageResult result)
        => Interlocked.Exchange(ref _pending, result);

    public bool TryTake(out IntelligenceCoverageResult? result)
    {
        result = Interlocked.Exchange(ref _pending, null);
        return result is not null;
    }
}
