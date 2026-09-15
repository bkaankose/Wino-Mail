using System.Threading.Tasks;

namespace Wino.Core.Synchronizers.Exchange.Streaming;

/// <summary>
/// One long-lived push channel for one Exchange account: the EWS streaming subscription or the
/// MAPI/HTTP notification session, chosen by the account's effective transport.
/// </summary>
internal interface IAccountNotificationListener
{
    Task StartAsync();
    void Stop();
}
