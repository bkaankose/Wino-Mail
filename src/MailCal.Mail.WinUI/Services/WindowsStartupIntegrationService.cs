using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Services;

namespace MailCal.Mail.WinUI.Services;

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
