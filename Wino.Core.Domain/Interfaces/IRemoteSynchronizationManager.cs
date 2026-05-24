using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Marker for a synchronization manager implementation that forwards work to
/// the packaged background synchronization host.
/// </summary>
public interface IRemoteSynchronizationManager : ISynchronizationManager
{
    Task ShutdownHostAsync();
}
