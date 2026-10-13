using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.iOS.Services;

/// <summary>iOS apps cannot launch at sign-in; both calls report <see cref="StartupBehaviorResult.Unavailable"/>.</summary>
public sealed class IOSStartupIntegrationService : IStartupIntegrationService
{
    public Task<StartupBehaviorResult> GetCurrentBehaviorAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(StartupBehaviorResult.Unavailable);

    public Task<StartupBehaviorResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StartupBehaviorResult.Unavailable);
    }
}
