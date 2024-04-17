using System.Threading.Tasks;
using Wino.Core.Domain.Models.AutoDiscovery;

namespace Wino.Core.Domain.Interfaces
{
    /// <summary>
    /// Searches for Auto Discovery settings for custom mail accounts.
    /// </summary>
    public interface IAutoDiscoveryService
    {
        /// <summary>
        /// Tries to return the best mail server settings using different techniques.
        /// </summary>
        /// <param name="mailAddress">Address to search settings for.</param>
        /// <returns>CustomServerInformation with only settings applied.</returns>
        Task<AutoDiscoverySettings> GetAutoDiscoverySettings(AutoDiscoveryMinimalSettings autoDiscoveryMinimalSettings);
    }
}
