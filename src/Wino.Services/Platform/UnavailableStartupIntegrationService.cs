#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.Platform;

/// <summary>For hosts whose apps cannot launch at sign-in (iOS, Android): both calls report <see cref="StartupBehaviorResult.Unavailable"/>.</summary>
public sealed class UnavailableStartupIntegrationService : IStartupIntegrationService
{
    public Task<StartupBehaviorResult> GetCurrentBehaviorAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(StartupBehaviorResult.Unavailable);

    public Task<StartupBehaviorResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StartupBehaviorResult.Unavailable);
    }
}
