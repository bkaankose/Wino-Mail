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

    /// <summary>Whether the channel is open right now, so server changes are being pushed.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// How many times an open channel has closed, for whatever reason. Changes made on the server
    /// while it was closed were not pushed, so a caller relying on push reconciles when this moves.
    /// </summary>
    int InterruptionCount { get; }
}
