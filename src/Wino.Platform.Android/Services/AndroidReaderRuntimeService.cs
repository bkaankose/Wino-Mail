using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Android.Services;

/// <summary>Mail is drawn by Wino's own renderer in the app process, so the reader is always available.</summary>
public sealed class AndroidReaderRuntimeService : IReaderRuntimeService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}
