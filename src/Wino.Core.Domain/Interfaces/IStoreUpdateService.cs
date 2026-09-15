using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IStoreUpdateService
{
    bool HasAvailableUpdate { get; }

    Task<bool> RefreshAvailabilityAsync();

    Task<bool> StartUpdateAsync();
}
