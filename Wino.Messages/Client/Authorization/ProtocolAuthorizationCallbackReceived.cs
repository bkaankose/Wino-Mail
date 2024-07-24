using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Messaging.Client.Authorization
{
    /// <summary>
    /// When Google authentication makes a callback to the app via protocol activation to the app.
    /// App will send this message back to server to continue authorization there.
    /// </summary>
    /// <param name="AuthorizationResponseUri">Callback Uri that Google returned.</param>
    public record ProtocolAuthorizationCallbackReceived(Uri AuthorizationResponseUri) : IClientMessage;
}
