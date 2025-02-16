using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Server
{
    /// <summary>
    /// This message is sent to server to kill itself when UWP app is terminating.
    /// </summary>
    public record TerminateServerRequested : IClientMessage;
}
