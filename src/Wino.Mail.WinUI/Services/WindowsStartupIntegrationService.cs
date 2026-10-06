using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Mail.WinUI.Services;

public sealed class WindowsStartupIntegrationService(NativeAppService nativeService) : IStartupIntegrationService
{
    public Task<StartupBehaviorResult> GetCurrentBehaviorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return nativeService.GetCurrentStartupBehaviorAsync();
    }

    public Task<StartupBehaviorResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return nativeService.ToggleStartupBehavior(isEnabled);
    }
}
