using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Apple.Services;

/// <summary>
/// Mail is read and composed with the native mail renderer, which runs inside the app process; there is no separate
/// content process that could be missing or fail to start.
/// </summary>
public sealed class AppleReaderRuntimeService : IReaderRuntimeService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}
