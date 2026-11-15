using System.Threading.Tasks;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

/// <summary>An on-premises Exchange synchronizer and its server change listener, the Exchange counterpart of IMAP IDLE.</summary>
public interface IExchangeSynchronizer
{
    /// <summary>
    /// The change listener state. Changes are also published as <c>ExchangeNotificationStateChanged</c> through the default messenger.
    /// </summary>
    ExchangeNotificationStatus NotificationStatus { get; }

    Task StartChangeListenerAsync();
    Task StopChangeListenerAsync();
}
