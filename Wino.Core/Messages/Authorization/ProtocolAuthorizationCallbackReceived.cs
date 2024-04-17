using System;

namespace Wino.Core.Messages.Authorization
{
    /// <summary>
    /// When Google authentication makes a callback to the app via protocol activation to the app.
    /// </summary>
    /// <param name="AuthorizationResponseUri">Callback Uri that Google returned.</param>
    public record ProtocolAuthorizationCallbackReceived(Uri AuthorizationResponseUri);
}
