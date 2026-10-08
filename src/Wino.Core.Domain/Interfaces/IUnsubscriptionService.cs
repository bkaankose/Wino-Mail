using System.Threading.Tasks;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Interfaces;

public interface IUnsubscriptionService
{
    /// <summary>
    /// Unsubscribes from the subscription using one-click method.
    /// </summary>
    /// <param name="info">Unsubscribtion information.</param>
    /// <returns>Whether the unsubscription is succeeded or not.</returns>
    Task<bool> OneClickUnsubscribeAsync(UnsubscribeInfo info);
}
