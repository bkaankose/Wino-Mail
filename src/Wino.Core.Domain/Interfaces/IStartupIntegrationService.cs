using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IStartupIntegrationService
{
    Task<StartupBehaviorResult> GetCurrentBehaviorAsync(CancellationToken cancellationToken = default);
    Task<StartupBehaviorResult> SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default);
}
