using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

public interface IStartMenuIntegrationService
{
    bool IsEnabled(WinoApplicationMode mode);
    Task SetEnabledAsync(WinoApplicationMode mode, bool enabled);
}
