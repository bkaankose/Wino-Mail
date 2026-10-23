using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Interfaces;

public interface IStartupIntegrationService
{
    Task<StartupBehaviorResult> GetCurrentBehaviorAsync(CancellationToken cancellationToken = default);
    Task<StartupBehaviorResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default);
}
